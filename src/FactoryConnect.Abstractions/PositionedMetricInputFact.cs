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

        MetricInputOwnershipValidator.Validate(
            streamId,
            fact,
            shiftOccurrenceId,
            productionDayId,
            nameof(fact));

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
