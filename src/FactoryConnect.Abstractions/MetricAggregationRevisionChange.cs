using System.Collections.ObjectModel;

namespace FactoryConnect.Abstractions;

public sealed record MetricAggregationRevisionChange
{
    public MetricAggregationRevisionChange(
        MetricAggregationCheckpoint revision,
        IEnumerable<ShiftOccurrenceId> shiftOccurrenceIds,
        IEnumerable<ProductionDayId> productionDayIds)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(shiftOccurrenceIds);
        ArgumentNullException.ThrowIfNull(productionDayIds);

        var shifts = shiftOccurrenceIds.ToArray();
        if (shifts.Any(static value => value is null))
        {
            throw new ArgumentException(
                "Aggregation revision changes cannot contain null shift occurrence identifiers.",
                nameof(shiftOccurrenceIds));
        }

        var productionDays = productionDayIds.ToArray();
        if (productionDays.Any(static value => value is null))
        {
            throw new ArgumentException(
                "Aggregation revision changes cannot contain null production-day identifiers.",
                nameof(productionDayIds));
        }

        Revision = revision;
        ShiftOccurrenceIds = new ReadOnlyCollection<ShiftOccurrenceId>(shifts.Distinct().ToArray());
        ProductionDayIds = new ReadOnlyCollection<ProductionDayId>(productionDays.Distinct().ToArray());
    }

    public MetricAggregationCheckpoint Revision { get; }

    public IReadOnlyList<ShiftOccurrenceId> ShiftOccurrenceIds { get; }

    public IReadOnlyList<ProductionDayId> ProductionDayIds { get; }
}

public interface IMetricAggregationRevisionReader
{
    ValueTask<MetricAggregationRevisionChange?> ReadNextAsync(
        MetricAggregationProcessorId processorId,
        MetricInputStreamId streamId,
        MetricAggregationCheckpoint? afterRevision,
        CancellationToken cancellationToken);

    ValueTask<MetricAggregationRevisionChange?> ReadExactAsync(
        MetricAggregationCheckpoint revision,
        CancellationToken cancellationToken);
}
