using FactoryConnect.Abstractions;
using FactoryConnect.Infrastructure;
using FactoryConnect.Persistence.SqlServer;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public abstract class AcquisitionContactAuthorityProviderConformanceTests
{
    [Fact]
    public async Task AuthorityIsAbsentBeforeFirstCommittedAcquisition()
    {
        var store = CreateStore();
        var streamId = StreamId();

        Assert.Null(await store.ReadAcquisitionContactAuthorityAsync(streamId));
        Assert.Null(await store.ReadCheckpointAsync(streamId));
    }

    [Fact]
    public async Task FirstEmptyCommitCreatesAuthorityWithoutRawHighWater()
    {
        var store = CreateStore();
        var streamId = StreamId();
        var checkpoint = new ObservationCheckpoint(streamId, 42, 101);
        var contactTime = Instant(10);

        await store.CommitAsync(
            new ObservationIngestionBatch(
                null,
                checkpoint,
                [],
                contactTime));

        var authority = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(authority);
        Assert.Equal(streamId, authority.ObservationStreamId);
        Assert.Equal(contactTime.UtcDateTime.Ticks, authority.SuccessfulContactTime.UtcDateTime.Ticks);
        Assert.Null(authority.RawAcceptedThrough);
        Assert.Equal(checkpoint, await store.ReadCheckpointAsync(streamId));
    }

    [Fact]
    public async Task EmptyAndDuplicateOnlyContactsPreserveRawHighWaterAndAdvanceChangedAuthorityRevision()
    {
        var store = CreateStore();
        var streamId = StreamId();
        var initial = InitialBatch(streamId, Instant(10));

        await store.CommitAsync(initial);
        var first = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(first);
        Assert.NotNull(first.RawAcceptedThrough);

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
        Assert.Equal(initial.Checkpoint, await store.ReadCheckpointAsync(streamId));
    }

    [Fact]
    public async Task OrdinaryNewRawAdvancesHighWaterAndChangesRevision()
    {
        var store = CreateStore();
        var streamId = StreamId();
        var initial = InitialBatch(streamId, Instant(10));
        var continuation = ContinuationBatch(streamId, initial.Checkpoint, Instant(20));

        await store.CommitAsync(initial);
        var before = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(before);
        Assert.NotNull(before.RawAcceptedThrough);

        await store.CommitAsync(continuation);
        var after = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(after);
        Assert.NotNull(after.RawAcceptedThrough);

        Assert.NotEqual(before.RawAcceptedThrough, after.RawAcceptedThrough);
        Assert.NotEqual(before.AcquisitionRevision, after.AcquisitionRevision);
        Assert.Equal(continuation.Checkpoint, await store.ReadCheckpointAsync(streamId));
    }

    [Fact]
    public async Task SameCheckpointContactMayMoveBackwardAndEquivalentInstantPreservesRevision()
    {
        var store = CreateStore();
        var streamId = StreamId();
        var initial = InitialBatch(streamId, Instant(10));

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

        await store.CommitAsync(
            new ObservationIngestionBatch(
                initial.Checkpoint,
                initial.Checkpoint,
                [],
                earlier.ToOffset(TimeSpan.FromHours(5.5))));
        var third = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(third);

        Assert.Equal(second.AcquisitionRevision, third.AcquisitionRevision);
        Assert.Equal(second.RawAcceptedThrough, third.RawAcceptedThrough);
        Assert.Equal(earlier.UtcDateTime.Ticks, third.SuccessfulContactTime.UtcDateTime.Ticks);
    }

    [Fact]
    public async Task ExactReplayIsIdempotentAndSupersededReplayIsRejected()
    {
        var store = CreateStore();
        var streamId = StreamId();
        var initial = InitialBatch(streamId, Instant(10));
        var continuation = ContinuationBatch(streamId, initial.Checkpoint, Instant(20));

        await store.CommitAsync(initial);
        await store.CommitAsync(continuation);
        var beforeReplay = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(beforeReplay);

        await store.CommitAsync(continuation);

        Assert.Equal(beforeReplay, await store.ReadAcquisitionContactAuthorityAsync(streamId));
        Assert.Equal(continuation.Checkpoint, await store.ReadCheckpointAsync(streamId));

        await store.CommitAsync(
            new ObservationIngestionBatch(
                continuation.Checkpoint,
                continuation.Checkpoint,
                [],
                Instant(30)));
        var supersedingAuthority = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(supersedingAuthority);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CommitAsync(continuation).AsTask());

        Assert.Equal(
            supersedingAuthority,
            await store.ReadAcquisitionContactAuthorityAsync(streamId));
        Assert.Equal(continuation.Checkpoint, await store.ReadCheckpointAsync(streamId));
    }

    [Fact]
    public async Task StaleChangedContactAndSameCheckpointNewRawRejectWithoutMutation()
    {
        var store = CreateStore();
        var streamId = StreamId();
        var initial = InitialBatch(streamId, Instant(10));

        await store.CommitAsync(initial);
        var before = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(before);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CommitAsync(
                new ObservationIngestionBatch(
                    null,
                    initial.Checkpoint,
                    initial.Observations,
                    Instant(20))).AsTask());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CommitAsync(
                new ObservationIngestionBatch(
                    initial.Checkpoint,
                    initial.Checkpoint,
                    [
                        new SequencedMachineObservation(
                            100,
                            Observation(streamId.MachineId, "new-raw")),
                    ],
                    Instant(20))).AsTask());

        Assert.Equal(before, await store.ReadAcquisitionContactAuthorityAsync(streamId));
        Assert.Equal(initial.Checkpoint, await store.ReadCheckpointAsync(streamId));
    }

    [Fact]
    public async Task PreCanceledCommitCreatesNoAuthorityOrCheckpoint()
    {
        var store = CreateStore();
        var streamId = StreamId();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.CommitAsync(
                InitialBatch(streamId, Instant(10)),
                cancellation.Token).AsTask());

        Assert.Null(await store.ReadAcquisitionContactAuthorityAsync(streamId));
        Assert.Null(await store.ReadCheckpointAsync(streamId));
    }

    [Fact]
    public async Task AuthoritiesRemainIsolatedByExactStreamIdentity()
    {
        var store = CreateStore();
        var machineId = MachineId.New();
        var firstStream = new ObservationStreamId(machineId, $"MTConnect:{Guid.NewGuid():N}");
        var secondStream = new ObservationStreamId(machineId, $"MTConnect:{Guid.NewGuid():N}");

        await store.CommitAsync(InitialBatch(firstStream, Instant(10)));
        await store.CommitAsync(InitialBatch(secondStream, Instant(20)));

        var first = await store.ReadAcquisitionContactAuthorityAsync(firstStream);
        var second = await store.ReadAcquisitionContactAuthorityAsync(secondStream);
        Assert.NotNull(first);
        Assert.NotNull(second);

        Assert.Equal(Instant(10).UtcDateTime.Ticks, first.SuccessfulContactTime.UtcDateTime.Ticks);
        Assert.Equal(Instant(20).UtcDateTime.Ticks, second.SuccessfulContactTime.UtcDateTime.Ticks);
        Assert.Equal(firstStream, first.ObservationStreamId);
        Assert.Equal(secondStream, second.ObservationStreamId);
    }

    protected abstract IObservationIngestionStore CreateStore();

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

    private static ObservationIngestionBatch ContinuationBatch(
        ObservationStreamId streamId,
        ObservationCheckpoint expectedCheckpoint,
        DateTimeOffset successfulContactTime) =>
        new(
            expectedCheckpoint,
            new ObservationCheckpoint(streamId, 42, 105),
            [
                new SequencedMachineObservation(
                    103,
                    Observation(streamId.MachineId, "execution-2")),
                new SequencedMachineObservation(
                    104,
                    Observation(streamId.MachineId, "load-2")),
            ],
            successfulContactTime);

    private static ObservationStreamId StreamId() =>
        new(MachineId.New(), $"MTConnect:{Guid.NewGuid():N}");

    private static DateTimeOffset Instant(int minute) =>
        new(2026, 9, 12, 4, minute, 0, TimeSpan.Zero);

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

public sealed class InMemoryAcquisitionContactAuthorityProviderConformanceTests :
    AcquisitionContactAuthorityProviderConformanceTests
{
    protected override IObservationIngestionStore CreateStore() =>
        new InMemoryObservationIngestionStore();
}

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerAcquisitionContactAuthorityProviderConformanceTests :
    AcquisitionContactAuthorityProviderConformanceTests,
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerAcquisitionContactAuthorityProviderConformanceTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    protected override IObservationIngestionStore CreateStore() =>
        new SqlServerObservationIngestionStore(_fixture.ConnectionString);
}
