using FactoryConnect.Abstractions;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public abstract class ObservationProcessingStoreConformanceTests
{
    protected abstract IObservationIngestionStore CreateStore();

    [Fact]
    public async Task ReadAsyncAssignsStableOrderedPositions()
    {
        var store = CreateStore();
        var streamId = StreamId();
        var ingestion = InitialBatch(streamId);

        await store.CommitAsync(ingestion);

        var firstRead = await Reader(store).ReadAsync(
            new ObservationReadRequest(streamId, null, 10));

        await store.CommitAsync(ingestion);

        var replayRead = await Reader(store).ReadAsync(
            new ObservationReadRequest(streamId, null, 10));

        Assert.Equal(
            [new ObservationPosition(1), new ObservationPosition(2)],
            firstRead.Observations.Select(item => item.Position));
        Assert.Equal(
            [101UL, 102UL],
            firstRead.Observations.Select(item => item.Sequence));
        Assert.Equal(
            firstRead.Observations,
            replayRead.Observations);
        Assert.Equal(firstRead.HasMore, replayRead.HasMore);
    }

    [Fact]
    public async Task ReadAsyncPagesAfterExclusivePosition()
    {
        var store = CreateStore();
        var streamId = StreamId();

        await store.CommitAsync(InitialBatch(streamId));

        var firstPage = await Reader(store).ReadAsync(
            new ObservationReadRequest(streamId, null, 1));
        var secondPage = await Reader(store).ReadAsync(
            new ObservationReadRequest(
                streamId,
                firstPage.Observations[0].Position,
                1));

        Assert.Single(firstPage.Observations);
        Assert.True(firstPage.HasMore);
        Assert.Equal(
            new ObservationPosition(1),
            firstPage.Observations[0].Position);
        Assert.Single(secondPage.Observations);
        Assert.False(secondPage.HasMore);
        Assert.Equal(
            new ObservationPosition(2),
            secondPage.Observations[0].Position);
    }

    [Fact]
    public async Task ReadAsyncReturnsEmptyBatchForUnknownStream()
    {
        var store = CreateStore();
        var streamId = StreamId();

        var batch = await Reader(store).ReadAsync(
            new ObservationReadRequest(streamId, null, 10));

        Assert.Equal(streamId, batch.StreamId);
        Assert.Empty(batch.Observations);
        Assert.False(batch.HasMore);
    }

    [Fact]
    public async Task ReadAsyncAcceptsMaximumBatchSize()
    {
        var store = CreateStore();
        var streamId = StreamId();

        await store.CommitAsync(InitialBatch(streamId));

        var batch = await Reader(store).ReadAsync(
            new ObservationReadRequest(
                streamId,
                null,
                int.MaxValue));

        Assert.Equal(2, batch.Observations.Count);
        Assert.False(batch.HasMore);
    }

    [Fact]
    public async Task PositionsRemainMonotonicAcrossInstanceTransition()
    {
        var store = CreateStore();
        var streamId = StreamId();
        var current = InitialCheckpoint(streamId);

        await store.CommitAsync(InitialBatch(streamId));
        await store.CommitAsync(
            new ObservationIngestionBatch(
                current,
                new ObservationCheckpoint(streamId, 84, 3),
                [
                    new SequencedMachineObservation(
                        1,
                        Observation(streamId.MachineId, "availability")),
                    new SequencedMachineObservation(
                        2,
                        Observation(streamId.MachineId, "execution")),
                ]));

        var batch = await Reader(store).ReadAsync(
            new ObservationReadRequest(streamId, null, 10));

        Assert.Equal(
            [
                new ObservationPosition(1),
                new ObservationPosition(2),
                new ObservationPosition(3),
                new ObservationPosition(4),
            ],
            batch.Observations.Select(item => item.Position));
        Assert.Equal(
            [42UL, 42UL, 84UL, 84UL],
            batch.Observations.Select(item => item.InstanceId));
    }

    [Fact]
    public async Task DurableReadsKeepStreamsIsolated()
    {
        var store = CreateStore();
        var first = StreamId();
        var second = new ObservationStreamId(
            first.MachineId,
            "MTConnect:CNC-02");

        await store.CommitAsync(InitialBatch(first));
        await store.CommitAsync(InitialBatch(second));

        var firstBatch = await Reader(store).ReadAsync(
            new ObservationReadRequest(first, null, 10));
        var secondBatch = await Reader(store).ReadAsync(
            new ObservationReadRequest(second, null, 10));

        Assert.Equal(2, firstBatch.Observations.Count);
        Assert.Equal(2, secondBatch.Observations.Count);
        Assert.All(
            firstBatch.Observations,
            item => Assert.Equal(first, item.StreamId));
        Assert.All(
            secondBatch.Observations,
            item => Assert.Equal(second, item.StreamId));
    }

    [Fact]
    public async Task ReadAsyncHonorsPreCanceledToken()
    {
        var store = CreateStore();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Reader(store).ReadAsync(
                new ObservationReadRequest(StreamId(), null, 10),
                cancellation.Token).AsTask());
    }

    [Fact]
    public async Task ReadProcessingCheckpointReturnsNullInitially()
    {
        var store = CreateStore();
        var streamId = StreamId();
        var processorId = new ObservationProcessorId("machine-state");

        var checkpoint = await Checkpoints(store).ReadCheckpointAsync(
            processorId,
            streamId);

        Assert.Null(checkpoint);
    }

    [Fact]
    public async Task CommitProcessingCheckpointCreatesInitialPosition()
    {
        var store = CreateStore();
        var streamId = StreamId();
        var checkpoint = ProcessingCheckpoint(
            "machine-state",
            streamId,
            1);

        await store.CommitAsync(InitialBatch(streamId));
        await Checkpoints(store).CommitAsync(
            new ObservationProcessingCommit(null, checkpoint));

        Assert.Equal(
            checkpoint,
            await Checkpoints(store).ReadCheckpointAsync(
                checkpoint.ProcessorId,
                streamId));
    }

    [Fact]
    public async Task InitialProcessingCommitRejectsNonNullExpectedCheckpoint()
    {
        var store = CreateStore();
        var streamId = StreamId();
        var expected = ProcessingCheckpoint(
            "machine-state",
            streamId,
            1);
        var checkpoint = ProcessingCheckpoint(
            "machine-state",
            streamId,
            2);

        await store.CommitAsync(InitialBatch(streamId));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Checkpoints(store).CommitAsync(
                new ObservationProcessingCommit(
                    expected,
                    checkpoint)).AsTask());

        Assert.Null(
            await Checkpoints(store).ReadCheckpointAsync(
                checkpoint.ProcessorId,
                streamId));
    }

    [Fact]
    public async Task ProcessingCommitAdvancesMatchingCheckpoint()
    {
        var store = CreateStore();
        var streamId = StreamId();
        var current = ProcessingCheckpoint(
            "machine-state",
            streamId,
            1);
        var next = ProcessingCheckpoint(
            "machine-state",
            streamId,
            2);

        await store.CommitAsync(InitialBatch(streamId));
        await Checkpoints(store).CommitAsync(
            new ObservationProcessingCommit(null, current));
        await Checkpoints(store).CommitAsync(
            new ObservationProcessingCommit(current, next));

        Assert.Equal(
            next,
            await Checkpoints(store).ReadCheckpointAsync(
                next.ProcessorId,
                streamId));
    }

    [Fact]
    public async Task ProcessingCommitRejectsStaleExpectedCheckpoint()
    {
        var store = CreateStore();
        var streamId = StreamId();
        var current = ProcessingCheckpoint(
            "machine-state",
            streamId,
            1);
        var next = ProcessingCheckpoint(
            "machine-state",
            streamId,
            2);

        await store.CommitAsync(InitialBatch(streamId));
        await Checkpoints(store).CommitAsync(
            new ObservationProcessingCommit(null, current));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Checkpoints(store).CommitAsync(
                new ObservationProcessingCommit(null, next)).AsTask());

        Assert.Equal(
            current,
            await Checkpoints(store).ReadCheckpointAsync(
                current.ProcessorId,
                streamId));
    }

    [Fact]
    public async Task ProcessingCommitRejectsUnknownDurablePosition()
    {
        var store = CreateStore();
        var streamId = StreamId();
        var checkpoint = ProcessingCheckpoint(
            "machine-state",
            streamId,
            3);

        await store.CommitAsync(InitialBatch(streamId));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Checkpoints(store).CommitAsync(
                new ObservationProcessingCommit(
                    null,
                    checkpoint)).AsTask());

        Assert.Null(
            await Checkpoints(store).ReadCheckpointAsync(
                checkpoint.ProcessorId,
                streamId));
    }

    [Fact]
    public async Task ProcessingCheckpointsKeepProcessorsIsolated()
    {
        var store = CreateStore();
        var streamId = StreamId();
        var machineState = ProcessingCheckpoint(
            "machine-state",
            streamId,
            1);
        var metricInput = ProcessingCheckpoint(
            "metric-input",
            streamId,
            2);

        await store.CommitAsync(InitialBatch(streamId));
        await Checkpoints(store).CommitAsync(
            new ObservationProcessingCommit(null, machineState));
        await Checkpoints(store).CommitAsync(
            new ObservationProcessingCommit(null, metricInput));

        Assert.Equal(
            machineState,
            await Checkpoints(store).ReadCheckpointAsync(
                machineState.ProcessorId,
                streamId));
        Assert.Equal(
            metricInput,
            await Checkpoints(store).ReadCheckpointAsync(
                metricInput.ProcessorId,
                streamId));
    }

    [Fact]
    public async Task ProcessingCheckpointsKeepStreamsIsolated()
    {
        var store = CreateStore();
        var first = StreamId();
        var second = new ObservationStreamId(
            first.MachineId,
            "MTConnect:CNC-02");
        var processorId = new ObservationProcessorId("machine-state");
        var firstCheckpoint = new ObservationProcessingCheckpoint(
            processorId,
            first,
            new ObservationPosition(1));
        var secondCheckpoint = new ObservationProcessingCheckpoint(
            processorId,
            second,
            new ObservationPosition(1));

        await store.CommitAsync(InitialBatch(first));
        await store.CommitAsync(InitialBatch(second));
        await Checkpoints(store).CommitAsync(
            new ObservationProcessingCommit(null, firstCheckpoint));
        await Checkpoints(store).CommitAsync(
            new ObservationProcessingCommit(null, secondCheckpoint));

        Assert.Equal(
            firstCheckpoint,
            await Checkpoints(store).ReadCheckpointAsync(
                processorId,
                first));
        Assert.Equal(
            secondCheckpoint,
            await Checkpoints(store).ReadCheckpointAsync(
                processorId,
                second));
    }

    [Fact]
    public async Task CheckpointResumeReadsOnlyUnprocessedObservations()
    {
        var store = CreateStore();
        var streamId = StreamId();
        var checkpoint = ProcessingCheckpoint(
            "machine-state",
            streamId,
            1);

        await store.CommitAsync(InitialBatch(streamId));
        await Checkpoints(store).CommitAsync(
            new ObservationProcessingCommit(null, checkpoint));

        var restored = await Checkpoints(store).ReadCheckpointAsync(
            checkpoint.ProcessorId,
            streamId);
        var batch = await Reader(store).ReadAsync(
            new ObservationReadRequest(
                streamId,
                restored?.Position,
                10));

        Assert.Single(batch.Observations);
        Assert.Equal(
            new ObservationPosition(2),
            batch.Observations[0].Position);
    }

    [Fact]
    public async Task ProcessingStoreHonorsPreCanceledToken()
    {
        var store = CreateStore();
        var streamId = StreamId();
        var processorId = new ObservationProcessorId("machine-state");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Checkpoints(store).ReadCheckpointAsync(
                processorId,
                streamId,
                cancellation.Token).AsTask());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Checkpoints(store).CommitAsync(
                new ObservationProcessingCommit(
                    null,
                    new ObservationProcessingCheckpoint(
                        processorId,
                        streamId,
                        new ObservationPosition(1))),
                cancellation.Token).AsTask());
    }

    private static IDurableObservationReader Reader(
        IObservationIngestionStore store) =>
        Assert.IsAssignableFrom<IDurableObservationReader>(store);

    private static IObservationProcessingCheckpointStore Checkpoints(
        IObservationIngestionStore store) =>
        Assert.IsAssignableFrom<
            IObservationProcessingCheckpointStore>(store);

    private static ObservationProcessingCheckpoint ProcessingCheckpoint(
        string processorId,
        ObservationStreamId streamId,
        ulong position) =>
        new(
            new ObservationProcessorId(processorId),
            streamId,
            new ObservationPosition(position));

    private static ObservationIngestionBatch InitialBatch(
        ObservationStreamId streamId) =>
        new(
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

    private static ObservationCheckpoint InitialCheckpoint(
        ObservationStreamId streamId) =>
        new(streamId, 42, 103);

    private static ObservationStreamId StreamId() =>
        new(MachineId.New(), "MTConnect:CNC-01");

    private static MachineObservation Observation(
        MachineId machineId,
        string address) =>
        new()
        {
            MachineId = machineId,
            Source = "MTConnect",
            Address = address,
            Type = SignalType.Enumeration,
            Value = "ACTIVE",
            Timestamp = DateTimeOffset.UnixEpoch,
        };
}
