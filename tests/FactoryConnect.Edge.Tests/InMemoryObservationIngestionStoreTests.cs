using FactoryConnect.Abstractions;
using FactoryConnect.Infrastructure;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class InMemoryObservationIngestionStoreTests
{
    [Fact]
    public async Task CommitAsyncStoresObservationsAndCheckpointTogether()
    {
        var store = new InMemoryObservationIngestionStore();
        var streamId = StreamId();
        var batch = Batch(streamId, instanceId: 42, nextSequence: 103);

        await store.CommitAsync(batch);

        Assert.Equal(
            batch.Checkpoint,
            await store.ReadCheckpointAsync(streamId));
        Assert.Equal(2, store.ReadObservations(streamId).Length);
    }

    [Fact]
    public async Task CommitAsyncIsIdempotent()
    {
        var store = new InMemoryObservationIngestionStore();
        var streamId = StreamId();
        var batch = Batch(streamId, instanceId: 42, nextSequence: 103);

        await store.CommitAsync(batch);
        await store.CommitAsync(batch);

        Assert.Equal(2, store.ReadObservations(streamId).Length);
        Assert.Equal(
            103UL,
            (await store.ReadCheckpointAsync(streamId))?.NextSequence);
    }

    [Fact]
    public async Task CommitAsyncRejectsCheckpointRegressionWithoutChangingState()
    {
        var store = new InMemoryObservationIngestionStore();
        var streamId = StreamId();

        await store.CommitAsync(
            Batch(streamId, instanceId: 42, nextSequence: 103));

        var regressingBatch = new ObservationIngestionBatch(
            new ObservationCheckpoint(streamId, 42, 102),
            []);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CommitAsync(regressingBatch).AsTask());

        Assert.Equal(
            103UL,
            (await store.ReadCheckpointAsync(streamId))?.NextSequence);
        Assert.Equal(2, store.ReadObservations(streamId).Length);
    }

    [Fact]
    public async Task CommitAsyncRejectsInvalidObservationWithoutPartialWrite()
    {
        var store = new InMemoryObservationIngestionStore();
        var streamId = StreamId();
        var otherMachineObservation = Observation(
            MachineId.New(),
            "execution");
        var batch = new ObservationIngestionBatch(
            new ObservationCheckpoint(streamId, 42, 102),
            [new SequencedMachineObservation(101, otherMachineObservation)]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CommitAsync(batch).AsTask());

        Assert.Null(await store.ReadCheckpointAsync(streamId));
        Assert.Empty(store.ReadObservations(streamId));
    }

    [Fact]
    public async Task CommitAsyncAllowsNewInstanceToStartAtLowerSequence()
    {
        var store = new InMemoryObservationIngestionStore();
        var streamId = StreamId();

        await store.CommitAsync(
            Batch(streamId, instanceId: 42, nextSequence: 103));
        await store.CommitAsync(
            new ObservationIngestionBatch(
                new ObservationCheckpoint(streamId, 43, 2),
                [new SequencedMachineObservation(
                    1,
                    Observation(streamId.MachineId, "availability"))]));

        var checkpoint = await store.ReadCheckpointAsync(streamId);

        Assert.Equal(43UL, checkpoint?.InstanceId);
        Assert.Equal(2UL, checkpoint?.NextSequence);
        Assert.Equal(3, store.ReadObservations(streamId).Length);
    }

    private static ObservationIngestionBatch Batch(
        ObservationStreamId streamId,
        ulong instanceId,
        ulong nextSequence)
    {
        return new ObservationIngestionBatch(
            new ObservationCheckpoint(
                streamId,
                instanceId,
                nextSequence),
            [
                new SequencedMachineObservation(
                    101,
                    Observation(streamId.MachineId, "execution")),
                new SequencedMachineObservation(
                    102,
                    Observation(streamId.MachineId, "load")),
            ]);
    }

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
            Type = SignalType.String,
            Value = "ACTIVE",
            Timestamp = DateTimeOffset.UnixEpoch,
        };
    }
}
