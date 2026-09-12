using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerAcquisitionAuthorityPhysicalConformanceTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private const string UInt64MaxLiteral = "18446744073709551615";
    private const string BeyondUInt64Literal = "18446744073709551616";
    private static readonly ulong[] InitialPositions = [1UL, 2UL];

    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerAcquisitionAuthorityPhysicalConformanceTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task MachineObservationPositionConstraintsRejectNullRangeAndDuplicateValues()
    {
        var streamId = StreamId();
        var store = CreateStore();
        await store.CommitAsync(InitialBatch(streamId, Instant(10)));

        await AssertSqlRejectedAsync(
            """
            UPDATE dbo.MachineObservation
            SET Position = NULL
            WHERE MachineId = @MachineId
              AND StreamKeyBinary = @StreamKeyBinary
              AND Position = 2;
            """,
            streamId);

        await AssertSqlRejectedAsync(
            """
            UPDATE dbo.MachineObservation
            SET Position = 0
            WHERE MachineId = @MachineId
              AND StreamKeyBinary = @StreamKeyBinary
              AND Position = 2;
            """,
            streamId);

        await AssertSqlRejectedAsync(
            $"""
            UPDATE dbo.MachineObservation
            SET Position = {BeyondUInt64Literal}
            WHERE MachineId = @MachineId
              AND StreamKeyBinary = @StreamKeyBinary
              AND Position = 2;
            """,
            streamId);

        await AssertSqlRejectedAsync(
            """
            UPDATE dbo.MachineObservation
            SET Position = 1
            WHERE MachineId = @MachineId
              AND StreamKeyBinary = @StreamKeyBinary
              AND Position = 2;
            """,
            streamId);

        var positions = await ReadPositionsAsync(streamId);
        Assert.Equal(InitialPositions, positions);
    }

    [Fact]
    public async Task AcquisitionAuthorityConstraintsEnforceUtcRangesAndSameStreamRawReference()
    {
        var firstStream = StreamId();
        var secondStream = StreamId();
        var store = CreateStore();

        await store.CommitAsync(InitialBatch(firstStream, Instant(10)));
        await store.CommitAsync(ThreeRawBatch(secondStream, Instant(20)));

        await AssertSqlRejectedAsync(
            """
            UPDATE dbo.AcquisitionContactAuthority
            SET SuccessfulContactTime = '2026-09-12T12:00:00+05:30'
            WHERE MachineId = @MachineId
              AND StreamKeyBinary = @StreamKeyBinary;
            """,
            firstStream);

        await AssertSqlRejectedAsync(
            """
            UPDATE dbo.AcquisitionContactAuthority
            SET RawAcceptedThrough = 0
            WHERE MachineId = @MachineId
              AND StreamKeyBinary = @StreamKeyBinary;
            """,
            firstStream);

        // Position 3 exists for the second stream, but not for the first.
        // The composite FK must reject cross-stream high-water references.
        await AssertSqlRejectedAsync(
            """
            UPDATE dbo.AcquisitionContactAuthority
            SET RawAcceptedThrough = 3
            WHERE MachineId = @MachineId
              AND StreamKeyBinary = @StreamKeyBinary;
            """,
            firstStream);

        await AssertSqlRejectedAsync(
            """
            UPDATE dbo.AcquisitionContactAuthority
            SET AcquisitionRevision = -1
            WHERE MachineId = @MachineId
              AND StreamKeyBinary = @StreamKeyBinary;
            """,
            firstStream);

        await AssertSqlRejectedAsync(
            $"""
            UPDATE dbo.AcquisitionContactAuthority
            SET AcquisitionRevision = {BeyondUInt64Literal}
            WHERE MachineId = @MachineId
              AND StreamKeyBinary = @StreamKeyBinary;
            """,
            firstStream);

        var authority = await store.ReadAcquisitionContactAuthorityAsync(firstStream);
        Assert.NotNull(authority);
        Assert.Equal(Instant(10).UtcDateTime.Ticks, authority.SuccessfulContactTime.UtcDateTime.Ticks);
        Assert.Equal(2UL, authority.RawAcceptedThrough?.Value);
    }

    [Fact]
    public async Task RawPositionExhaustionAllowsDuplicateOnlyContactAndRejectsNewRawAtomically()
    {
        var streamId = StreamId();
        var store = CreateStore();
        var initial = InitialBatch(streamId, Instant(10));
        await store.CommitAsync(initial);

        await ExecuteAsync(
            """
            UPDATE dbo.AcquisitionContactAuthority
            SET RawAcceptedThrough = NULL
            WHERE MachineId = @MachineId
              AND StreamKeyBinary = @StreamKeyBinary;
            """,
            streamId);

        await ExecuteAsync(
            $"""
            UPDATE dbo.MachineObservation
            SET Position = {UInt64MaxLiteral}
            WHERE MachineId = @MachineId
              AND StreamKeyBinary = @StreamKeyBinary
              AND Position = 2;
            """,
            streamId);

        await ExecuteAsync(
            $"""
            UPDATE dbo.AcquisitionContactAuthority
            SET RawAcceptedThrough = {UInt64MaxLiteral}
            WHERE MachineId = @MachineId
              AND StreamKeyBinary = @StreamKeyBinary;
            """,
            streamId);

        var beforeDuplicateOnly = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(beforeDuplicateOnly);
        Assert.Equal(ulong.MaxValue, beforeDuplicateOnly.RawAcceptedThrough?.Value);

        await store.CommitAsync(
            new ObservationIngestionBatch(
                initial.Checkpoint,
                initial.Checkpoint,
                initial.Observations,
                Instant(20)));

        var afterDuplicateOnly = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(afterDuplicateOnly);
        Assert.Equal(ulong.MaxValue, afterDuplicateOnly.RawAcceptedThrough?.Value);
        Assert.NotEqual(
            beforeDuplicateOnly.AcquisitionRevision,
            afterDuplicateOnly.AcquisitionRevision);

        var checkpointBeforeFailure = await store.ReadCheckpointAsync(streamId);
        var authorityBeforeFailure = afterDuplicateOnly;
        var positionsBeforeFailure = await ReadPositionsAsync(streamId);

        var advancingCheckpoint = new ObservationCheckpoint(streamId, 42, 104);
        var newRaw = new ObservationIngestionBatch(
            initial.Checkpoint,
            advancingCheckpoint,
            [
                new SequencedMachineObservation(
                    103,
                    Observation(streamId.MachineId, "execution-3")),
            ],
            Instant(30));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CommitAsync(newRaw).AsTask());

        Assert.Equal(checkpointBeforeFailure, await store.ReadCheckpointAsync(streamId));
        Assert.Equal(
            authorityBeforeFailure,
            await store.ReadAcquisitionContactAuthorityAsync(streamId));
        Assert.Equal(positionsBeforeFailure, await ReadPositionsAsync(streamId));
    }

    private SqlServerObservationIngestionStore CreateStore() =>
        new(_fixture.ConnectionString);

    private async Task AssertSqlRejectedAsync(
        string commandText,
        ObservationStreamId streamId)
    {
        await Assert.ThrowsAsync<SqlException>(
            () => ExecuteAsync(commandText, streamId));
    }

    private async Task ExecuteAsync(
        string commandText,
        ObservationStreamId streamId)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        AddStreamParameters(command, streamId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<ulong[]> ReadPositionsAsync(ObservationStreamId streamId)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Position
            FROM dbo.MachineObservation
            WHERE MachineId = @MachineId
              AND StreamKeyBinary = @StreamKeyBinary
            ORDER BY Position;
            """;
        AddStreamParameters(command, streamId);

        var values = new List<ulong>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(SqlServerUInt64.Materialize(reader.GetDecimal(0)));
        }

        return values.ToArray();
    }

    private static void AddStreamParameters(
        SqlCommand command,
        ObservationStreamId streamId)
    {
        command.Parameters.AddWithValue("@MachineId", streamId.MachineId.Value);
        command.Parameters.AddWithValue(
            "@StreamKeyBinary",
            OrdinalStringKeyCodec.Encode(streamId.StreamKey));
    }

    private static ObservationIngestionBatch InitialBatch(
        ObservationStreamId streamId,
        DateTimeOffset successfulContactTime) =>
        new(
            null,
            new ObservationCheckpoint(streamId, 42, 103),
            [
                new SequencedMachineObservation(
                    101,
                    Observation(streamId.MachineId, "execution")),
                new SequencedMachineObservation(
                    102,
                    Observation(streamId.MachineId, "load")),
            ],
            successfulContactTime);

    private static ObservationIngestionBatch ThreeRawBatch(
        ObservationStreamId streamId,
        DateTimeOffset successfulContactTime) =>
        new(
            null,
            new ObservationCheckpoint(streamId, 42, 104),
            [
                new SequencedMachineObservation(
                    101,
                    Observation(streamId.MachineId, "execution")),
                new SequencedMachineObservation(
                    102,
                    Observation(streamId.MachineId, "load")),
                new SequencedMachineObservation(
                    103,
                    Observation(streamId.MachineId, "availability")),
            ],
            successfulContactTime);

    private static ObservationStreamId StreamId() =>
        new(MachineId.New(), $"MTConnect:{Guid.NewGuid():N}");

    private static DateTimeOffset Instant(int minute) =>
        new(2026, 9, 12, 6, minute, 0, TimeSpan.Zero);

    private static MachineObservation Observation(
        MachineId machineId,
        string address) =>
        new()
        {
            MachineId = machineId,
            Source = "MTConnect",
            Address = address,
            Type = SignalType.Text,
            Value = "ACTIVE",
            Timestamp = DateTimeOffset.UnixEpoch,
        };
}
