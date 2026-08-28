using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.Core;

public sealed class OperationalMetricProjectionProcessingRuntime
{
    private readonly MetricAggregationProcessorId _sourceProcessorId;
    private readonly MetricInputStreamId _sourceStreamId;
    private readonly IOperationalMetricEvaluationBatchSource _source;
    private readonly OperationalMetricProjectionFactory _projectionFactory;
    private readonly IOperationalMetricProjectionStore _store;
    private OperationalMetricProjectionCheckpoint? _checkpoint;
    private bool _checkpointRestored;

    public OperationalMetricProjectionProcessingRuntime(
        OperationalMetricProjectionProcessorId processorId,
        MetricAggregationProcessorId sourceProcessorId,
        MetricInputStreamId sourceStreamId,
        IOperationalMetricEvaluationBatchSource source,
        OperationalMetricProjectionFactory projectionFactory,
        IOperationalMetricProjectionStore store)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(sourceProcessorId);
        ArgumentNullException.ThrowIfNull(sourceStreamId);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(projectionFactory);
        ArgumentNullException.ThrowIfNull(store);

        ProcessorId = processorId;
        _sourceProcessorId = sourceProcessorId;
        _sourceStreamId = sourceStreamId;
        _source = source;
        _projectionFactory = projectionFactory;
        _store = store;
    }

    public OperationalMetricProjectionProcessorId ProcessorId { get; }

    public async ValueTask<int> RunCycleAsync(CancellationToken cancellationToken = default)
    {
        await RestoreCheckpointAsync(cancellationToken);

        var request = new OperationalMetricEvaluationBatchRequest(
            _sourceProcessorId,
            _sourceStreamId,
            _checkpoint?.SourceRevision);
        var batch = await _source.ReadAsync(request, cancellationToken);
        if (batch is null)
        {
            return 0;
        }

        ValidateBatchIdentity(batch);

        if (_checkpoint is not null)
        {
            var comparison = batch.SourceRevision.Position.CompareTo(
                _checkpoint.SourceRevision.Position);
            if (comparison < 0)
            {
                throw new InvalidOperationException(
                    "Evaluation batch source revision precedes the durable projection checkpoint.");
            }

            if (comparison == 0)
            {
                await ValidateReplayAsync(batch, cancellationToken);
                return 0;
            }
        }

        var changed = new List<OperationalMetricProjection>(batch.Evaluations.Count);
        foreach (var evaluation in batch.Evaluations)
        {
            var projection = _projectionFactory.Create(evaluation);
            var existing = await _store.ReadProjectionAsync(
                ProcessorId,
                projection.Key,
                cancellationToken);

            if (existing is null ||
                !OperationalMetricProjectionEquivalence.AreEquivalent(existing, projection))
            {
                changed.Add(projection);
            }
        }

        var nextCheckpoint = new OperationalMetricProjectionCheckpoint(
            ProcessorId,
            batch.SourceRevision);
        await _store.CommitAsync(
            new OperationalMetricProjectionCommit(
                ProcessorId,
                _checkpoint,
                nextCheckpoint,
                changed),
            cancellationToken);

        _checkpoint = nextCheckpoint;
        return changed.Count;
    }

    private async ValueTask RestoreCheckpointAsync(CancellationToken cancellationToken)
    {
        if (_checkpointRestored)
        {
            return;
        }

        _checkpoint = await _store.ReadCheckpointAsync(
            ProcessorId,
            _sourceStreamId,
            cancellationToken);

        if (_checkpoint is not null &&
            _checkpoint.SourceRevision.ProcessorId != _sourceProcessorId)
        {
            throw new InvalidOperationException(
                "Durable projection checkpoint belongs to a different FC-026 aggregation processor.");
        }

        _checkpointRestored = true;
    }

    private void ValidateBatchIdentity(OperationalMetricEvaluationBatch batch)
    {
        if (batch.SourceRevision.ProcessorId != _sourceProcessorId ||
            batch.SourceRevision.StreamId != _sourceStreamId)
        {
            throw new InvalidOperationException(
                "Evaluation batch belongs to a different FC-026 processor or stream than the projection runtime.");
        }
    }

    private async ValueTask ValidateReplayAsync(
        OperationalMetricEvaluationBatch batch,
        CancellationToken cancellationToken)
    {
        foreach (var evaluation in batch.Evaluations)
        {
            var projected = _projectionFactory.Create(evaluation);
            var existing = await _store.ReadProjectionAsync(
                ProcessorId,
                projected.Key,
                cancellationToken);
            if (existing is null ||
                !OperationalMetricProjectionEquivalence.AreEquivalent(existing, projected))
            {
                throw new InvalidDataException(
                    "Replay at the durable projection checkpoint does not match persisted projection state.");
            }
        }
    }
}
