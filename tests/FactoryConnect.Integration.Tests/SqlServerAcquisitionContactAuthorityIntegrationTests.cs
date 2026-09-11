using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerAcquisitionContactAuthorityIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private static readonly ulong[] TwoPositions = [1UL, 2UL];
    private static readonly ulong[] FourPositions = [1UL, 2UL, 3UL, 4UL];

    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerAcquisitionContactAuthorityIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task FirstEmptyCommitPersistsAuthorityWithoutRawHighWaterAcrossStoreRecreation()
    {
        var streamId = StreamId();
        var checkpoint = new ObservationCheckpoint(streamId, 42, 101);
        var contactTime = Instant(10);
        var store = CreateStore();

        Assert.Null(await store.ReadAcquisitionContactAuthorityAsync(streamId));

        await store.CommitAsync(
            new ObservationIngestionBatch(
                null,
                checkpoint,
                [],
                contactTime));

        var recreated = CreateStore();
        var authority = await recreated.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(authority);

        Assert.Equal(streamId, authority.ObservationStreamId);
        Assert.Equal(contactTime.UtcDateTime.Ticks, authority.SuccessfulContactTime.UtcDateTime.Ticks);
        Assert.Null(authority.RawAcceptedThrough);
        Assert.Equal(checkpoint, await recreated.ReadCheckpointAsync(streamId));
        Assert.Equal(0, await ReadObservationCountAsync(streamId));
    }

    [Fact]
    public async Task RawHighWaterUsesDurablePositionsAndEmptyAndDuplicateOnlyContactsPreserveIt()
    {
        var streamId = StreamId();
        var initial = InitialBatch(streamId, Instant(10));
        var store = CreateStore();

        await store.CommitAsync(initial);
        var first = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(first);
        Assert.NotNull(first.RawAcceptedThrough);
        Assert.Equal(2UL, first.RawAcceptedThrough.Value);
        Assert.Equal(TwoPositions, await ReadPositionsAsync(streamId));

        await store.CommitAsync(
            new ObservationIngestionBatch(
                initial.Checkpoint,
                initial.Checkpoint,
                [],
                Instant(20)));
        var afterEmpty = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(afterEmpty);
        Assert.Equal(first.RawAcceptedThrough, afterEmpty.RawAcceptedThrough);
        Assert.NotEqual(first.AcquisitionRevision, afterEmpty.AcquisitionRevision);
        Assert.Equal(TwoPositions, await ReadPositionsAsync(streamId));

        await store.CommitAsync(
            new ObservationIngestionBatch(
                initial.Checkpoint,
                initial.Checkpoint,
                initial.Observations,
                Instant(30)));
        var afterDuplicates = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(afterDuplicates);
        Assert.Equal(first.RawAcceptedThrough, afterDuplicates.RawAcceptedThrough);
        Assert.NotEqual(afterEmpty.AcquisitionRevision, afterDuplicates.AcquisitionRevision);
        Assert.Equal(TwoPositions, await ReadPositionsAsync(streamId));
    }

    [Fact]
    public async Task SameCheckpointContactCanMoveClockBackwardAndEquivalentInstantPreservesRevision()
    {
        var streamId = StreamId();
        var initial = InitialBatch(streamId, Instant(10));
        var store = CreateStore();

        await store.CommitAsync(initial);
        var first = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(first);

        var earlier = Instant(5);
        await store.CommitAsync(
            new ObservationIngestionBatch(
                initial.Checkpoint,
                initial.Checkpoint,
                [],
                earlier));
        var second = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(second);

        Assert.Equal(earlier.UtcDateTime.Ticks, second.SuccessfulContactTime.UtcDateTime.Ticks);
        Assert.Equal(first.RawAcceptedThrough, second.RawAcceptedThrough);
        Assert.NotEqual(first.AcquisitionRevision, second.AcquisitionRevision);

        var equivalent = earlier.ToOffset(TimeSpan.FromHours(5.5));
        await store.CommitAsync(
            new ObservationIngestionBatch(
                initial.Checkpoint,
                initial.Checkpoint,
                [],
                equivalent));
        var third = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(third);

        Assert.Equal(second.AcquisitionRevision, third.AcquisitionRevision);
        Assert.Equal(second.RawAcceptedThrough, third.RawAcceptedThrough);
        Assert.Equal(earlier.UtcDateTime.Ticks, third.SuccessfulContactTime.UtcDateTime.Ticks);
        Assert.Equal(TwoPositions, await ReadPositionsAsync(streamId));
    }

    [Fact]
    public async Task ExactReplayAfterStoreRecreationIsIdempotentButSupersededReplayIsRejected()
    {
        var streamId = StreamId();
        var initial = InitialBatch(streamId, Instant(10));
        var nextCheckpoint = new ObservationCheckpoint(streamId, 42, 105);
        var continuation = new ObservationIngestionBatch(
            initial.Checkpoint,
            nextCheckpoint,
            [
                new SequencedMachineObservation(
                    103,
                    Observation(streamId.MachineId, "execution-2")),
                new SequencedMachineObservation(
                    104,
                    Observation(streamId.MachineId, "load-2")),
            ],
            Instant(20));
        var store = CreateStore();

        await store.CommitAsync(initial);
        await store.CommitAsync(continuation);
        var beforeReplay = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(beforeReplay);
        Assert.Equal(FourPositions, await ReadPositionsAsync(streamId));

        var recreated = CreateStore();
        await recreated.CommitAsync(continuation);

        Assert.Equal(
            beforeReplay,
            await recreated.ReadAcquisitionContactAuthorityAsync(streamId));
        Assert.Equal(nextCheckpoint, await recreated.ReadCheckpointAsync(streamId));
        Assert.Equal(FourPositions, await ReadPositionsAsync(streamId));

        await recreated.CommitAsync(
            new ObservationIngestionBatch(
                nextCheckpoint,
                nextCheckpoint,
                [],
                Instant(30)));
        var supersedingAuthority = await recreated.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(supersedingAuthority);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => recreated.CommitAsync(continuation).AsTask());

        Assert.Equal(
            supersedingAuthority,
            await recreated.ReadAcquisitionContactAuthorityAsync(streamId));
        Assert.Equal(nextCheckpoint, await recreated.ReadCheckpointAsync(streamId));
        Assert.Equal(FourPositions, await ReadPositionsAsync(streamId));
    }

    [Fact]
    public async Task StaleChangedContactAndSameCheckpointNewRawAreRejectedAtomically()
    {
        var streamId = StreamId();
        var initial = InitialBatch(streamId, Instant(10));
        var store = CreateStore();

        await store.CommitAsync(initial);
        var authorityBefore = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(authorityBefore);

        var staleChangedContact = new ObservationIngestionBatch(
            null,
            initial.Checkpoint,
            initial.Observations,
            Instant(20));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CommitAsync(staleChangedContact).AsTask());

        var sameCheckpointNewRaw = new ObservationIngestionBatch(
            initial.Checkpoint,
            initial.Checkpoint,
            [
                new SequencedMachineObservation(
                    100,
                    Observation(streamId.MachineId, "new-raw")),
            ],
            Instant(20));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CommitAsync(sameCheckpointNewRaw).AsTask());

        Assert.Equal(authorityBefore, await store.ReadAcquisitionContactAuthorityAsync(streamId));
        Assert.Equal(initial.Checkpoint, await store.ReadCheckpointAsync(streamId));
        Assert.Equal(TwoPositions, await ReadPositionsAsync(streamId));
    }

    [Fact]
    public async Task AcquisitionAuthorityRemainsIsolatedByExactStreamIdentity()
    {
        var machineId = MachineId.New();
        var firstStream = new ObservationStreamId(machineId, "MTConnect:CNC-01");
        var secondStream = new ObservationStreamId(machineId, "MTConnect:CNC-02");
        var store = CreateStore();

        await store.CommitAsync(InitialBatch(firstStream, Instant(10)));
        await store.CommitAsync(InitialBatch(secondStream, Instant(20)));

        var first = await store.ReadAcquisitionContactAuthorityAsync(firstStream);
        var second = await store.ReadAcquisitionContactAuthorityAsync(secondStream);
        Assert.NotNull(first);
        Assert.NotNull(second);

        Assert.Equal(Instant(10).UtcDateTime.Ticks, first.SuccessfulContactTime.UtcDateTime.Ticks);
        Assert.Equal(Instant(20).UtcDateTime.Ticks, second.SuccessfulContactTime.UtcDateTime.Ticks);
        Assert.Equal(TwoPositions, await ReadPositionsAsync(firstStream));
        Assert.Equal(TwoPositions, await ReadPositionsAsync(secondStream));
    }

    [Fact]
    public async Task RevisionExhaustionAllowsUnchangedPayloadAndRejectsChangeAtomically()
    {
        var streamId = StreamId();
        var initial = InitialBatch(streamId, Instant(10));
        var store = CreateStore();

        await store.CommitAsync(initial);
        await ForceRevisionAsync(streamId, ulong.MaxValue);

        var before = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(before);
        Assert.Equal(ulong.MaxValue, before.AcquisitionRevision.Value);
        var checkpointBefore = await store.ReadCheckpointAsync(streamId);
        var positionsBefore = await ReadPositionsAsync(streamId);

        await store.CommitAsync(
            new ObservationIngestionBatch(
                initial.Checkpoint,
                initial.Checkpoint,
                [],
                before.SuccessfulContactTime));

        Assert.Equal(before, await store.ReadAcquisitionContactAuthorityAsync(streamId));
        Assert.Equal(checkpointBefore, await store.ReadCheckpointAsync(streamId));
        Assert.Equal(positionsBefore, await ReadPositionsAsync(streamId));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CommitAsync(
                new ObservationIngestionBatch(
                    initial.Checkpoint,
                    initial.Checkpoint,
                    [],
                    Instant(20))).AsTask());

        Assert.Equal(before, await store.ReadAcquisitionContactAuthorityAsync(streamId));
        Assert.Equal(checkpointBefore, await store.ReadCheckpointAsync(streamId));
        Assert.Equal(positionsBefore, await ReadPositionsAsync(streamId));
    }

    [Fact]
    public async Task PreCanceledCommitCreatesNoCheckpointRawOrAuthorityState()
    {
        var streamId = StreamId();
        var store = CreateStore();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.CommitAsync(
                InitialBatch(streamId, Instant(10)),
                cancellation.Token).AsTask());

        Assert.Null(await store.ReadCheckpointAsync(streamId));
        Assert.Null(await store.ReadAcquisitionContactAuthorityAsync(streamId));
        Assert.Empty(await ReadPositionsAsync(streamId));
    }

    private SqlServerObservationIngestionStore CreateStore() =>
        new(_fixture.ConnectionString);

    private async Task<int> ReadObservationCountAsync(ObservationStreamId streamId)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM dbo.MachineObservation
            WHERE MachineId = @MachineId
              AND StreamKeyBinary = @StreamKeyBinary;
            """;
        AddStreamParameters(command, streamId);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
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

    private async Task ForceRevisionAsync(
        ObservationStreamId streamId,
        ulong revision)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dbo.AcquisitionContactAuthority
            SET AcquisitionRevision = @AcquisitionRevision
            WHERE MachineId = @MachineId
              AND StreamKeyBinary = @StreamKeyBinary;
            """;
        AddStreamParameters(command, streamId);
        command.Parameters.Add(
            SqlServerUInt64.CreateParameter(
                "@AcquisitionRevision",
                revision));
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
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

    private static ObservationStreamId StreamId() =>
        new(MachineId.New(), $"MTConnect:{Guid.NewGuid():N}");

    private static DateTimeOffset Instant(int minute) =>
        new(2026, 9, 11, 8, minute, 0, TimeSpan.Zero);

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
