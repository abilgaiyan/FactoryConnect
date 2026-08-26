using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Tests;

public sealed class ObservationProcessingRuntimeTests
{
    [Fact]
    public async Task RunCycleAsyncRestoresCheckpointAndReadsAfterIt()
    {
        var streamId = StreamId();
        var processor = new RecordingProcessor();
        var restored = Checkpoint(processor.ProcessorId, streamId, 10);
        var store = new RecordingCheckpointStore(restored);
        var reader = new RecordingReader(Batch(streamId, 11, 12));
        var runtime = Runtime(reader, store, processor, streamId);

        await runtime.RunCycleAsync();

        var request = Assert.Single(reader.Requests);
        Assert.Equal(restored.Position, request.AfterPosition);
        Assert.Equal(2, processor.Observations.Count);
        Assert.Equal(new ObservationPosition(12), store.Current?.Position);
    }

    [Fact]
    public async Task InitialCycleCommitsFromNullCheckpoint()
    {
        var streamId = StreamId();
        var processor = new RecordingProcessor();
        var store = new RecordingCheckpointStore();
        var runtime = Runtime(
            new RecordingReader(Batch(streamId, 1)),
            store,
            processor,
            streamId);

        await runtime.RunCycleAsync();

        var commit = Assert.Single(store.Commits);
        Assert.Null(commit.ExpectedCheckpoint);
        Assert.Equal(new ObservationPosition(1), commit.Checkpoint.Position);
    }

    [Fact]
    public async Task EmptyCycleDoesNotProcessOrCommit()
    {
        var streamId = StreamId();
        var processor = new RecordingProcessor();
        var store = new RecordingCheckpointStore();
        var runtime = Runtime(
            new RecordingReader(EmptyBatch(streamId)),
            store,
            processor,
            streamId);

        var result = await runtime.RunCycleAsync();

        Assert.Empty(result.Observations);
        Assert.Empty(processor.Observations);
        Assert.Empty(store.Commits);
    }

    [Fact]
    public async Task SuccessfulCycleAdvancesNextReadCursor()
    {
        var streamId = StreamId();
        var processor = new RecordingProcessor();
        var store = new RecordingCheckpointStore();
        var reader = new RecordingReader(
            Batch(streamId, 1),
            Batch(streamId, 2));
        var runtime = Runtime(reader, store, processor, streamId);

        await runtime.RunCycleAsync();
        await runtime.RunCycleAsync();

        Assert.Null(reader.Requests[0].AfterPosition);
        Assert.Equal(
            new ObservationPosition(1),
            reader.Requests[1].AfterPosition);
        Assert.Equal(new ObservationPosition(2), store.Current?.Position);
    }

    [Fact]
    public async Task ProcessorFailureDoesNotAdvanceCheckpoint()
    {
        var streamId = StreamId();
        var processor = new FailOnceProcessor();
        var store = new RecordingCheckpointStore();
        var reader = new RecordingReader(
            Batch(streamId, 1),
            Batch(streamId, 1));
        var runtime = Runtime(reader, store, processor, streamId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.RunCycleAsync());

        await runtime.RunCycleAsync();

        Assert.Equal(2, reader.Requests.Count);
        Assert.All(
            reader.Requests,
            request => Assert.Null(request.AfterPosition));
        Assert.Single(store.Commits);
        Assert.Equal(2, processor.InvocationCount);
    }

    [Fact]
    public async Task CheckpointFailureLeavesBatchEligibleForReprocessing()
    {
        var streamId = StreamId();
        var processor = new RecordingProcessor();
        var store = new FailOnceCheckpointStore();
        var reader = new RecordingReader(
            Batch(streamId, 1),
            Batch(streamId, 1));
        var runtime = Runtime(reader, store, processor, streamId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.RunCycleAsync());

        await runtime.RunCycleAsync();

        Assert.Equal(2, processor.InvocationCount);
        Assert.Equal(2, reader.Requests.Count);
        Assert.All(
            reader.Requests,
            request => Assert.Null(request.AfterPosition));
        Assert.Equal(new ObservationPosition(1), store.Current?.Position);
    }

    [Fact]
    public async Task CheckpointIsRestoredOnlyOncePerRuntime()
    {
        var streamId = StreamId();
        var store = new RecordingCheckpointStore();
        var runtime = Runtime(
            new RecordingReader(
                EmptyBatch(streamId),
                EmptyBatch(streamId)),
            store,
            new RecordingProcessor(),
            streamId);

        await runtime.RunCycleAsync();
        await runtime.RunCycleAsync();

        Assert.Equal(1, store.ReadCount);
    }

    [Fact]
    public async Task RunCycleAsyncPropagatesCancellationWithoutWork()
    {
        var streamId = StreamId();
        var processor = new RecordingProcessor();
        var store = new RecordingCheckpointStore();
        var runtime = Runtime(
            new RecordingReader(EmptyBatch(streamId)),
            store,
            processor,
            streamId);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runtime.RunCycleAsync(cancellation.Token));

        Assert.Empty(processor.Observations);
        Assert.Empty(store.Commits);
    }

    [Fact]
    public async Task RunAsyncStopsAfterCancellationWhileIdle()
    {
        var streamId = StreamId();
        using var cancellation = new CancellationTokenSource();
        var processor = new RecordingProcessor();
        var store = new RecordingCheckpointStore();
        var reader = new RecordingReader(
            cancellation.Cancel,
            EmptyBatch(streamId));
        var runtime = Runtime(reader, store, processor, streamId);

        await runtime.RunAsync(cancellation.Token);

        Assert.Single(reader.Requests);
        Assert.Empty(processor.Observations);
        Assert.Empty(store.Commits);
    }

    [Fact]
    public void OptionsRejectInvalidValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ObservationProcessingRuntimeOptions(
                0,
                TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ObservationProcessingRuntimeOptions(
                1,
                TimeSpan.Zero));
    }

    private static ObservationProcessingRuntime Runtime(
        IDurableObservationReader reader,
        IObservationProcessingCheckpointStore store,
        IObservationProcessor processor,
        ObservationStreamId streamId) =>
        new(
            reader,
            store,
            processor,
            streamId,
            new ObservationProcessingRuntimeOptions(
                10,
                TimeSpan.FromMilliseconds(1)));

    private static ObservationProcessingCheckpoint Checkpoint(
        ObservationProcessorId processorId,
        ObservationStreamId streamId,
        ulong position) =>
        new(
            processorId,
            streamId,
            new ObservationPosition(position));

    private static ObservationReadBatch EmptyBatch(
        ObservationStreamId streamId) =>
        new(streamId, [], hasMore: false);

    private static ObservationReadBatch Batch(
        ObservationStreamId streamId,
        params ulong[] positions) =>
        new(
            streamId,
            positions
                .Select(
                    position => new DurableMachineObservation(
                        new ObservationPosition(position),
                        streamId,
                        42,
                        position + 100,
                        Observation(streamId.MachineId)))
                .ToArray(),
            hasMore: false);

    private static ObservationStreamId StreamId() =>
        new(MachineId.New(), "MTConnect:CNC-01");

    private static MachineObservation Observation(MachineId machineId) =>
        new()
        {
            MachineId = machineId,
            Source = "MTConnect",
            Address = "execution",
            Type = SignalType.Enumeration,
            Value = "ACTIVE",
            Timestamp = DateTimeOffset.UnixEpoch,
        };

    private sealed class RecordingReader : IDurableObservationReader
    {
        private readonly Queue<ObservationReadBatch> _batches;
        private readonly Action? _onRead;

        public RecordingReader(params ObservationReadBatch[] batches)
            : this(null, batches)
        {
        }

        public RecordingReader(
            Action? onRead,
            params ObservationReadBatch[] batches)
        {
            _onRead = onRead;
            _batches = new Queue<ObservationReadBatch>(batches);
        }

        public List<ObservationReadRequest> Requests { get; } = [];

        public ValueTask<ObservationReadBatch> ReadAsync(
            ObservationReadRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            _onRead?.Invoke();

            return ValueTask.FromResult(_batches.Dequeue());
        }
    }

    private class RecordingCheckpointStore :
        IObservationProcessingCheckpointStore
    {
        public RecordingCheckpointStore(
            ObservationProcessingCheckpoint? initial = null)
        {
            Current = initial;
        }

        public ObservationProcessingCheckpoint? Current { get; protected set; }

        public int ReadCount { get; private set; }

        public List<ObservationProcessingCommit> Commits { get; } = [];

        public ValueTask<ObservationProcessingCheckpoint?>
            ReadCheckpointAsync(
                ObservationProcessorId processorId,
                ObservationStreamId streamId,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;

            return ValueTask.FromResult(Current);
        }

        public virtual ValueTask CommitAsync(
            ObservationProcessingCommit commit,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commits.Add(commit);
            Current = commit.Checkpoint;

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailOnceCheckpointStore :
        RecordingCheckpointStore
    {
        private bool _failNext = true;

        public override ValueTask CommitAsync(
            ObservationProcessingCommit commit,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_failNext)
            {
                _failNext = false;

                throw new InvalidOperationException(
                    "Checkpoint commit failed.");
            }

            return base.CommitAsync(commit, cancellationToken);
        }
    }

    private sealed class RecordingProcessor : IObservationProcessor
    {
        public ObservationProcessorId ProcessorId { get; } =
            new("machine-state");

        public int InvocationCount { get; private set; }

        public List<DurableMachineObservation> Observations { get; } = [];

        public ValueTask ProcessAsync(
            IReadOnlyList<DurableMachineObservation> observations,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvocationCount++;
            Observations.AddRange(observations);

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailOnceProcessor : IObservationProcessor
    {
        private bool _failNext = true;

        public ObservationProcessorId ProcessorId { get; } =
            new("machine-state");

        public int InvocationCount { get; private set; }

        public ValueTask ProcessAsync(
            IReadOnlyList<DurableMachineObservation> observations,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvocationCount++;

            if (_failNext)
            {
                _failNext = false;

                throw new InvalidOperationException(
                    "Processing failed.");
            }

            return ValueTask.CompletedTask;
        }
    }
}
