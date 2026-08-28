using System.Collections.ObjectModel;

namespace FactoryConnect.Abstractions;

public sealed record OperationalMetricEvaluationBatchRequest
{
    public OperationalMetricEvaluationBatchRequest(
        MetricAggregationProcessorId sourceProcessorId,
        MetricInputStreamId sourceStreamId,
        MetricAggregationCheckpoint? knownRevision)
    {
        ArgumentNullException.ThrowIfNull(sourceProcessorId);
        ArgumentNullException.ThrowIfNull(sourceStreamId);

        if (knownRevision is not null &&
            (knownRevision.ProcessorId != sourceProcessorId ||
             knownRevision.StreamId != sourceStreamId))
        {
            throw new ArgumentException(
                "Known evaluation revision must belong to the requested FC-026 processor and stream.",
                nameof(knownRevision));
        }

        SourceProcessorId = sourceProcessorId;
        SourceStreamId = sourceStreamId;
        KnownRevision = knownRevision;
    }

    public MetricAggregationProcessorId SourceProcessorId { get; }

    public MetricInputStreamId SourceStreamId { get; }

    public MetricAggregationCheckpoint? KnownRevision { get; }
}

public sealed record OperationalMetricEvaluationBatch
{
    public OperationalMetricEvaluationBatch(
        MetricAggregationCheckpoint sourceRevision,
        IEnumerable<OperationalMetricEvaluation> evaluations)
    {
        ArgumentNullException.ThrowIfNull(sourceRevision);
        ArgumentNullException.ThrowIfNull(evaluations);

        var snapshot = evaluations.ToArray();
        if (snapshot.Any(static evaluation => evaluation is null))
        {
            throw new ArgumentException(
                "Evaluation batches cannot contain null evaluations.",
                nameof(evaluations));
        }

        if (snapshot.Any(evaluation =>
            evaluation.SourceRevision != sourceRevision ||
            evaluation.Key.MachineId != sourceRevision.StreamId.MachineId))
        {
            throw new ArgumentException(
                "Every logical evaluation must belong to the coherent batch source revision and machine stream.",
                nameof(evaluations));
        }

        var duplicateKey = snapshot
            .GroupBy(static evaluation => evaluation.Key)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateKey is not null)
        {
            throw new ArgumentException(
                "Evaluation batches cannot contain duplicate evaluation keys.",
                nameof(evaluations));
        }

        SourceRevision = sourceRevision;
        Evaluations = new ReadOnlyCollection<OperationalMetricEvaluation>(snapshot);
    }

    public MetricAggregationCheckpoint SourceRevision { get; }

    public IReadOnlyList<OperationalMetricEvaluation> Evaluations { get; }
}

public interface IOperationalMetricEvaluationBatchSource
{
    ValueTask<OperationalMetricEvaluationBatch?> ReadAsync(
        OperationalMetricEvaluationBatchRequest request,
        CancellationToken cancellationToken);
}
