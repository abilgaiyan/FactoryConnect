using FactoryConnect.Abstractions;

namespace FactoryConnect.Core;

public sealed class MetricAggregationProcessingRuntime
{
    private readonly IMetricInputReader _reader;
    private readonly IMetricAggregationStore _store;
    private readonly MetricInputStreamId _streamId;
    private readonly int _batchSize;
    private MetricAggregationCheckpoint? _checkpoint;
    private bool _checkpointRestored;

    public MetricAggregationProcessingRuntime(
        MetricAggregationProcessorId processorId,
        IMetricInputReader reader,
        IMetricAggregationStore store,
        MetricInputStreamId streamId,
        int batchSize)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(streamId);

        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(batchSize),
                "Aggregation batch size must be greater than zero.");
        }

        ProcessorId = processorId;
        _reader = reader;
        _store = store;
        _streamId = streamId;
        _batchSize = batchSize;
    }

    public MetricAggregationProcessorId ProcessorId { get; }

    public async ValueTask<int> RunCycleAsync(CancellationToken cancellationToken = default)
    {
        await RestoreCheckpointAsync(cancellationToken);

        var request = MetricInputReadRequest.FromCheckpoint(
            ProcessorId,
            _streamId,
            _checkpoint,
            _batchSize);
        var batch = await _reader.ReadAsync(request, cancellationToken);

        if (batch.StreamId != _streamId || batch.AfterPosition != _checkpoint?.Position)
        {
            throw new InvalidOperationException(
                "Metric input reader returned a batch outside the requested aggregation window.");
        }

        if (batch.Facts.Count > _batchSize)
        {
            throw new InvalidOperationException(
                "Metric input reader returned more facts than the requested maximum.");
        }

        if (batch.ThroughPosition is null)
        {
            return 0;
        }

        if (_checkpoint is not null && batch.ThroughPosition <= _checkpoint.Position)
        {
            return 0;
        }

        var nextCheckpoint = new MetricAggregationCheckpoint(
            ProcessorId,
            _streamId,
            batch.ThroughPosition);
        var commit = new MetricAggregationCommit(
            ProcessorId,
            _checkpoint,
            nextCheckpoint,
            batch.Facts);

        await _store.CommitAsync(commit, cancellationToken);

        _checkpoint = nextCheckpoint;
        return batch.Facts.Count;
    }

    private async ValueTask RestoreCheckpointAsync(CancellationToken cancellationToken)
    {
        if (_checkpointRestored)
        {
            return;
        }

        _checkpoint = await _store.ReadCheckpointAsync(
            ProcessorId,
            _streamId,
            cancellationToken);
        _checkpointRestored = true;
    }
}
