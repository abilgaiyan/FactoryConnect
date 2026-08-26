using FactoryConnect.Abstractions;

namespace FactoryConnect.Core;

public sealed class ObservationProcessingRuntime
{
    private readonly IDurableObservationReader _reader;
    private readonly IObservationProcessingCheckpointStore _checkpointStore;
    private readonly IObservationProcessor _processor;
    private readonly ObservationStreamId _streamId;
    private readonly ObservationProcessingRuntimeOptions _options;
    private ObservationProcessingCheckpoint? _checkpoint;
    private bool _checkpointRestored;

    public ObservationProcessingRuntime(
        IDurableObservationReader reader,
        IObservationProcessingCheckpointStore checkpointStore,
        IObservationProcessor processor,
        ObservationStreamId streamId,
        ObservationProcessingRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(checkpointStore);
        ArgumentNullException.ThrowIfNull(processor);
        ArgumentNullException.ThrowIfNull(streamId);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(processor.ProcessorId);

        _reader = reader;
        _checkpointStore = checkpointStore;
        _processor = processor;
        _streamId = streamId;
        _options = options;
    }

    public async Task<ObservationReadBatch> RunCycleAsync(
        CancellationToken cancellationToken = default)
    {
        await RestoreCheckpointAsync(cancellationToken);

        var batch = await _reader.ReadAsync(
            new ObservationReadRequest(
                _streamId,
                _checkpoint?.Position,
                _options.BatchSize),
            cancellationToken);

        if (batch.Observations.Count == 0)
        {
            return batch;
        }

        await _processor.ProcessAsync(
            batch.Observations,
            cancellationToken);

        var nextCheckpoint = new ObservationProcessingCheckpoint(
            _processor.ProcessorId,
            _streamId,
            batch.Observations[^1].Position);

        await _checkpointStore.CommitAsync(
            new ObservationProcessingCommit(
                _checkpoint,
                nextCheckpoint),
            cancellationToken);

        _checkpoint = nextCheckpoint;

        return batch;
    }

    public async Task RunAsync(
        CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var batch = await RunCycleAsync(cancellationToken);

            if (batch.Observations.Count > 0 ||
                cancellationToken.IsCancellationRequested)
            {
                continue;
            }

            try
            {
                await Task.Delay(
                    _options.PollingInterval,
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async ValueTask RestoreCheckpointAsync(
        CancellationToken cancellationToken)
    {
        if (_checkpointRestored)
        {
            return;
        }

        _checkpoint =
            await _checkpointStore.ReadCheckpointAsync(
                _processor.ProcessorId,
                _streamId,
                cancellationToken);
        _checkpointRestored = true;
    }
}
