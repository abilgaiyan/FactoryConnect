namespace FactoryConnect.Abstractions;

public sealed record PositionedMetricInputFact
{
    public PositionedMetricInputFact(
        MetricInputStreamId streamId,
        MetricInputPosition position,
        DurableMetricInputFact fact,
        ShiftOccurrenceId shiftOccurrenceId,
        ProductionDayId productionDayId)
    {
        ArgumentNullException.ThrowIfNull(streamId);
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(shiftOccurrenceId);
        ArgumentNullException.ThrowIfNull(productionDayId);

        if (fact.Id.IsEmpty)
        {
            throw new ArgumentException(
                "Metric input fact identifier must not be empty.",
                nameof(fact));
        }

        if (fact.MachineId != streamId.MachineId)
        {
            throw new ArgumentException(
                "Metric input fact must belong to the metric input stream machine.",
                nameof(fact));
        }

        if (fact.SiteId != shiftOccurrenceId.SiteId ||
            fact.SiteId != productionDayId.SiteId)
        {
            throw new ArgumentException(
                "Metric input fact and temporal ownership must belong to the same site.",
                nameof(fact));
        }

        if (fact.ShiftId != shiftOccurrenceId.ShiftId)
        {
            throw new ArgumentException(
                "Metric input fact shift must match its shift occurrence ownership.",
                nameof(fact));
        }

        if (fact.ShiftScheduleAssignmentId is not null &&
            fact.ShiftScheduleAssignmentId != shiftOccurrenceId.ShiftScheduleAssignmentId)
        {
            throw new ArgumentException(
                "Metric input fact shift schedule lineage must match its shift occurrence ownership.",
                nameof(fact));
        }

        StreamId = streamId;
        Position = position;
        Fact = fact;
        ShiftOccurrenceId = shiftOccurrenceId;
        ProductionDayId = productionDayId;
    }

    public MetricInputStreamId StreamId { get; }

    public MetricInputPosition Position { get; }

    public DurableMetricInputFact Fact { get; }

    public ShiftOccurrenceId ShiftOccurrenceId { get; }

    public ProductionDayId ProductionDayId { get; }
}
