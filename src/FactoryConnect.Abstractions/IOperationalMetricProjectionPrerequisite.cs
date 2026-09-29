namespace FactoryConnect.Abstractions;

/// <summary>
/// Prepares the next exact FC-026 revision for its first operational projection.
/// A null result leaves the projection checkpoint unchanged.
/// </summary>
public interface IOperationalMetricProjectionPrerequisite
{
    ValueTask<MetricAggregationCheckpoint?> PrepareAsync(
        OperationalMetricEvaluationBatchRequest request,
        CancellationToken cancellationToken);
}
