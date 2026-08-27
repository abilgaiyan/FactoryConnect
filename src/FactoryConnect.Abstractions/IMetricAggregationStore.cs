namespace FactoryConnect.Abstractions;

public interface IMetricAggregationStore
{
    ValueTask<MetricAggregationCheckpoint?> ReadCheckpointAsync(
        MetricAggregationProcessorId processorId,
        MetricInputStreamId streamId,
        CancellationToken cancellationToken);

    ValueTask<MetricAggregateValue?> ReadShiftAggregateAsync(
        MetricAggregationProcessorId processorId,
        ShiftMetricAggregateKey key,
        CancellationToken cancellationToken);

    ValueTask<MetricAggregateValue?> ReadProductionDayAggregateAsync(
        MetricAggregationProcessorId processorId,
        ProductionDayMetricAggregateKey key,
        CancellationToken cancellationToken);

    ValueTask CommitAsync(
        MetricAggregationCommit commit,
        CancellationToken cancellationToken);
}
