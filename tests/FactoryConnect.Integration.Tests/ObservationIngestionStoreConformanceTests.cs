using FactoryConnect.Abstractions;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public abstract class ObservationIngestionStoreConformanceTests
{
    protected abstract IObservationIngestionStore CreateStore();

    protected abstract int ReadObservationCount(
        IObservationIngestionStore store,
        ObservationStreamId streamId);

    [Fact]
    public async Task CommitAsyncStoresObservationsAndCheckpointTogether()
    {
        var store = CreateStore();
        var streamId = StreamId();
        var batch = InitialBatch(streamId);

        await store.CommitAsync(batch);

        Assert.Equal(
            batch.Checkpoint,
            await store.ReadCheckpointAsync(streamId));
        Assert.Equal(2, ReadObservationCount(store, streamId));
    }

    [Fact]
    public async Task FirstCommitRejectsNonNullExpectedCheckpoint()
    {
        var store = CreateStore();
        var streamId = StreamId();
        var expected = new ObservationCheckpoint(streamId, 42, 100);
        var checkpoint = InitialCheckpoint(streamId);
        var batch = new ObservationIngestionBatch(
            expected,
            checkpoint,
            []);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CommitAsync(batch).AsTask());

        Assert.Null(await store.ReadCheckpointAsync(streamId));
        Assert.Equal(0, ReadObservationCount(store, streamId));
    }

    [Fact]
    public async Task CommitAsyncAdvancesMatchingCheckpointWithNewObservations()
    {
        var store = CreateStore();
        var streamId = StreamId();
        var current = InitialCheckpoint(streamId);
        var next = new ObservationCheckpoint(streamId, 42, 105);
        var continuation = new ObservationIngestionBatch(
            current,
            next,
            [
                new SequencedMachineObservation(
                    103,
                    Observation(streamId.MachineId, "execution")),
                new SequencedMachineObservation(
                    104,
                    Observation(streamId.MachineId, "load")),
            ]);

        await store.CommitAsync(InitialBatch(streamId));
        await store.CommitAsync(continuation);

        Assert.Equal(next, await store.ReadCheckpointAsync(streamId));
        Assert.Equal(4, ReadObservationCount(store, streamId));
    }

    [Fact]
    public async Task CommitAsyncIsIdempotentAfterUncertainAcknowledgement()
    {
        var store = CreateStore();
        var streamId = StreamId();
        var batch = InitialBatch(streamId);

        await store.CommitAsync(batch);
        await store.CommitAsync(batch);

        Assert.Equal(2, ReadObservationCount(store, streamId));
        Assert.Equal(
            batch.Checkpoint,
            await store.ReadCheckpointAsync(streamId));
    }

    [Fact]
    public async Task IdempotentReplayCannotAddObservationAtCommittedCheckpoint()
    {
        var store = CreateStore();
        var streamId = StreamId();
        var current = InitialCheckpoint(streamId);
        var augmentedReplay = new ObservationIngestionBatch(
            null,
            current,
            [new SequencedMachineObservation(
                100,
                Observation(streamId.MachineId, "availability"))]);

        await store.CommitAsync(InitialBatch(streamId));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CommitAsync(augmentedReplay).AsTask());

        Assert.Equal(
            current,
            await store.ReadCheckpointAsync(streamId));
        Assert.Equal(2, ReadObservationCount(store, streamId));
    }

    [Fact]
    public async Task CommitAsyncRejectsCheckpointRegressionWithoutChangingState()
    {
        var store = CreateStore();
        var streamId = StreamId();
        var current = InitialCheckpoint(streamId);

        await store.CommitAsync(InitialBatch(streamId));

        var regressingBatch = new ObservationIngestionBatch(
            current,
            new ObservationCheckpoint(streamId, 42, 102),
            []);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CommitAsync(regressingBatch).AsTask());

        Assert.Equal(
            current,
            await store.ReadCheckpointAsync(streamId));
        Assert.Equal(2, ReadObservationCount(store, streamId));
    }

    [Fact]
    public async Task CommitAsyncRejectsInvalidObservationWithoutPartialWrite()
    {
        var store = CreateStore();
        var streamId = StreamId();
        var otherMachineObservation = Observation(
            MachineId.New(),
            "execution");
        var batch = new ObservationIngestionBatch(
            null,
            new ObservationCheckpoint(streamId, 42, 102),
            [new SequencedMachineObservation(101, otherMachineObservation)]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CommitAsync(batch).AsTask());

        Assert.Null(await store.ReadCheckpointAsync(streamId));
        Assert.Equal(0, ReadObservationCount(store, streamId));
    }

    [Fact]
    public async Task CommitAsyncAllowsEmptyBatchToAdvanceCheckpoint()
    {
        var store = CreateStore();
        var streamId = StreamId();
        var current = InitialCheckpoint(streamId);
        var next = new ObservationCheckpoint(streamId, 42, 111);

        await store.CommitAsync(InitialBatch(streamId));
        await store.CommitAsync(
            new ObservationIngestionBatch(current, next, []));

        Assert.Equal(next, await store.ReadCheckpointAsync(streamId));
        Assert.Equal(2, ReadObservationCount(store, streamId));
    }

    [Fact]
    public async Task CommitAsyncAcceptsIdenticalDuplicateWithinBatchOnce()
    {
        var store = CreateStore();
        var streamId = StreamId();
        var checkpoint = new ObservationCheckpoint(streamId, 42, 102);
        var observation = Observation(streamId.MachineId, "execution");
        var batch = new ObservationIngestionBatch(
            null,
            checkpoint,
            [
                new SequencedMachineObservation(101, observation),
                new SequencedMachineObservation(101, observation),
            ]);

        await store.CommitAsync(batch);

        Assert.Equal(checkpoint, await store.ReadCheckpointAsync(streamId));
        Assert.Equal(1, ReadObservationCount(store, streamId));
    }

    [Fact]
    public async Task CommitAsyncRejectsConflictingDuplicateAtomically()
    {
        var store = CreateStore();
        var streamId = StreamId();
        var checkpoint = new ObservationCheckpoint(streamId, 42, 102);
        var batch = new ObservationIngestionBatch(
            null,
            checkpoint,
            [
                new SequencedMachineObservation(
                    101,
                    Observation(streamId.MachineId, "execution")),
                new SequencedMachineObservation(
                    101,
                    Observation(streamId.MachineId, "load")),
            ]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CommitAsync(batch).AsTask());

        Assert.Null(await store.ReadCheckpointAsync(streamId));
        Assert.Equal(0, ReadObservationCount(store, streamId));
    }

    [Fact]
    public async Task CommitAsyncKeepsStreamsIsolated()
    {
        var store = CreateStore();
        var machineId = MachineId.New();
        var first = new ObservationStreamId(
            machineId,
            "MTConnect:CNC-01");
        var second = new ObservationStreamId(
            machineId,
            "MTConnect:CNC-02");

        await store.CommitAsync(InitialBatch(first));
        await store.CommitAsync(InitialBatch(second));

        Assert.Equal(2, ReadObservationCount(store, first));
        Assert.Equal(2, ReadObservationCount(store, second));
        Assert.NotEqual(
            await store.ReadCheckpointAsync(first),
            await store.ReadCheckpointAsync(second));
    }

    [Fact]
    public async Task CommitAsyncHonorsPreCanceledTokenWithoutChangingState()
    {
        var store = CreateStore();
        var streamId = StreamId();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.CommitAsync(
                InitialBatch(streamId),
                cancellation.Token).AsTask());

        Assert.Null(await store.ReadCheckpointAsync(streamId));
        Assert.Equal(0, ReadObservationCount(store, streamId));
    }

    [Fact]
    public async Task CommitAsyncRejectsSequenceEqualToNextSequence()
    {
        var store = CreateStore();
        var streamId = StreamId();
        var checkpoint = new ObservationCheckpoint(streamId, 42, 102);
        var batch = new ObservationIngestionBatch(
            null,
            checkpoint,
            [new SequencedMachineObservation(
                102,
                Observation(streamId.MachineId, "execution"))]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CommitAsync(batch).AsTask());

        Assert.Null(await store.ReadCheckpointAsync(streamId));
        Assert.Equal(0, ReadObservationCount(store, streamId));
    }

    [Fact]
    public async Task CommitAsyncRejectsStaleExpectedCheckpointAtomically()
    {
        var store = CreateStore();
        var streamId = StreamId();
        var current = InitialCheckpoint(streamId);
        var stale = new ObservationCheckpoint(streamId, 42, 102);
        var replacement = new ObservationCheckpoint(streamId, 43, 2);
        var staleBatch = new ObservationIngestionBatch(
            stale,
            replacement,
            [new SequencedMachineObservation(
                1,
                Observation(streamId.MachineId, "availability"))]);

        await store.CommitAsync(InitialBatch(streamId));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CommitAsync(staleBatch).AsTask());

        Assert.Equal(
            current,
            await store.ReadCheckpointAsync(streamId));
        Assert.Equal(2, ReadObservationCount(store, streamId));
    }

    [Fact]
    public async Task CommitAsyncAllowsExplicitInstanceTransition()
    {
        var store = CreateStore();
        var streamId = StreamId();
        var current = InitialCheckpoint(streamId);
        var replacement = new ObservationCheckpoint(streamId, 43, 2);

        await store.CommitAsync(InitialBatch(streamId));
        await store.CommitAsync(
            new ObservationIngestionBatch(
                current,
                replacement,
                [new SequencedMachineObservation(
                    1,
                    Observation(streamId.MachineId, "availability"))]));

        Assert.Equal(
            replacement,
            await store.ReadCheckpointAsync(streamId));
        Assert.Equal(3, ReadObservationCount(store, streamId));
    }

    private static ObservationIngestionBatch InitialBatch(
        ObservationStreamId streamId)
    {
        return new ObservationIngestionBatch(
            null,
            InitialCheckpoint(streamId),
            [
                new SequencedMachineObservation(
                    101,
                    Observation(streamId.MachineId, "execution")),
                new SequencedMachineObservation(
                    102,
                    Observation(streamId.MachineId, "load")),
            ]);
    }

    private static ObservationCheckpoint InitialCheckpoint(
        ObservationStreamId streamId) =>
        new(streamId, 42, 103);

    private static ObservationStreamId StreamId() =>
        new(MachineId.New(), "MTConnect:CNC-01");

    private static MachineObservation Observation(
        MachineId machineId,
        string address)
    {
        return new MachineObservation
        {
            MachineId = machineId,
            Source = "MTConnect",
            Address = address,
            Type = SignalType.Text,
            Value = "ACTIVE",
            Timestamp = DateTimeOffset.UnixEpoch,
        };
    }
}
