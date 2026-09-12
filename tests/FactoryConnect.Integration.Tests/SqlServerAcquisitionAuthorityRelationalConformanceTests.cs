using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerAcquisitionAuthorityRelationalConformanceTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private const string BeyondUInt64Literal = "18446744073709551616";

    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerAcquisitionAuthorityRelationalConformanceTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AuthorityRequiresCheckpointAndRemainsOneRowPerExactStream()
    {
        var missingCheckpointStream = StreamId();

        await AssertSqlRejectedAsync(
            """
            INSERT INTO dbo.AcquisitionContactAuthority
            (
                MachineId,
                StreamKeyBinary,
                SuccessfulContactTime,
                RawAcceptedThrough,
                AcquisitionRevision
            )
            VALUES
            (
                @MachineId,
                @StreamKeyBinary,
                '2026-09-12T07:00:00+00:00',
                NULL,
                0
            );
            """,
            missingCheckpointStream);

        var streamId = StreamId();
        var store = new SqlServerObservationIngestionStore(_fixture.ConnectionString);
        await store.CommitAsync(
            new ObservationIngestionBatch(
                null,
                new ObservationCheckpoint(streamId, 42, 101),
                [],
                new DateTimeOffset(2026, 9, 12, 7, 10, 0, TimeSpan.Zero)));

        var authority = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(authority);
        Assert.Equal(0UL, authority.AcquisitionRevision.Value);

        await AssertSqlRejectedAsync(
            """
            INSERT INTO dbo.AcquisitionContactAuthority
            (
                MachineId,
                StreamKeyBinary,
                SuccessfulContactTime,
                RawAcceptedThrough,
                AcquisitionRevision
            )
            VALUES
            (
                @MachineId,
                @StreamKeyBinary,
                '2026-09-12T07:11:00+00:00',
                NULL,
                1
            );
            """,
            streamId);

        Assert.Equal(
            authority,
            await store.ReadAcquisitionContactAuthorityAsync(streamId));
    }

    [Fact]
    public async Task AuthorityPreventsDeletingItsCheckpointWithoutRawRows()
    {
        var streamId = StreamId();
        var store = new SqlServerObservationIngestionStore(_fixture.ConnectionString);
        var checkpoint = new ObservationCheckpoint(streamId, 42, 101);

        await store.CommitAsync(
            new ObservationIngestionBatch(
                null,
                checkpoint,
                [],
                new DateTimeOffset(2026, 9, 12, 7, 15, 0, TimeSpan.Zero)));

        var authorityBefore = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(authorityBefore);
        Assert.Null(authorityBefore.RawAcceptedThrough);
        Assert.Equal(0L, await CountRawObservationsAsync(streamId));

        await AssertSqlRejectedAsync(
            """
            DELETE FROM dbo.ObservationStreamCheckpoint
            WHERE MachineId = @MachineId
              AND StreamKeyBinary = @StreamKeyBinary;
            """,
            streamId);

        Assert.Equal(checkpoint, await store.ReadCheckpointAsync(streamId));
        Assert.Equal(
            authorityBefore,
            await store.ReadAcquisitionContactAuthorityAsync(streamId));
        Assert.Equal(0L, await CountRawObservationsAsync(streamId));
    }

    [Fact]
    public async Task AuthorityPreventsDeletingReferencedRawObservation()
    {
        var streamId = StreamId();
        var store = new SqlServerObservationIngestionStore(_fixture.ConnectionString);
        var checkpoint = new ObservationCheckpoint(streamId, 42, 102);
        var batch = new ObservationIngestionBatch(
            null,
            checkpoint,
            [
                new SequencedMachineObservation(
                    101,
                    new MachineObservation
                    {
                        MachineId = streamId.MachineId,
                        Source = "MTConnect",
                        Address = "execution",
                        Type = SignalType.Text,
                        Value = "ACTIVE",
                        Timestamp = DateTimeOffset.UnixEpoch,
                    }),
            ],
            new DateTimeOffset(2026, 9, 12, 7, 16, 0, TimeSpan.Zero));

        await store.CommitAsync(batch);
        var authorityBefore = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(authorityBefore);
        Assert.Equal(1UL, authorityBefore.RawAcceptedThrough?.Value);
        Assert.Equal(1L, await CountRawObservationsAsync(streamId));

        await AssertSqlRejectedAsync(
            """
            DELETE FROM dbo.MachineObservation
            WHERE MachineId = @MachineId
              AND StreamKeyBinary = @StreamKeyBinary
              AND Position = 1;
            """,
            streamId);

        Assert.Equal(checkpoint, await store.ReadCheckpointAsync(streamId));
        Assert.Equal(
            authorityBefore,
            await store.ReadAcquisitionContactAuthorityAsync(streamId));
        Assert.Equal(1L, await CountRawObservationsAsync(streamId));
    }

    [Fact]
    public async Task RawAcceptedThroughRejectsValuesAboveUInt64Range()
    {
        var streamId = StreamId();
        var store = new SqlServerObservationIngestionStore(_fixture.ConnectionString);
        await store.CommitAsync(
            new ObservationIngestionBatch(
                null,
                new ObservationCheckpoint(streamId, 42, 101),
                [],
                new DateTimeOffset(2026, 9, 12, 7, 20, 0, TimeSpan.Zero)));

        await AssertSqlRejectedAsync(
            $"""
            UPDATE dbo.AcquisitionContactAuthority
            SET RawAcceptedThrough = {BeyondUInt64Literal}
            WHERE MachineId = @MachineId
              AND StreamKeyBinary = @StreamKeyBinary;
            """,
            streamId);

        var authority = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(authority);
        Assert.Null(authority.RawAcceptedThrough);
    }

    private async Task AssertSqlRejectedAsync(
        string commandText,
        ObservationStreamId streamId)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        AddStreamParameters(command, streamId);

        await Assert.ThrowsAsync<SqlException>(
            () => command.ExecuteNonQueryAsync());
    }

    private async Task<long> CountRawObservationsAsync(
        ObservationStreamId streamId)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT_BIG(*)
            FROM dbo.MachineObservation
            WHERE MachineId = @MachineId
              AND StreamKeyBinary = @StreamKeyBinary;
            """;
        AddStreamParameters(command, streamId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
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

    private static ObservationStreamId StreamId() =>
        new(MachineId.New(), $"MTConnect:{Guid.NewGuid():N}");
}
