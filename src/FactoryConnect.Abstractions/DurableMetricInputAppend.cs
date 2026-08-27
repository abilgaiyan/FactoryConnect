namespace FactoryConnect.Abstractions;

public sealed record DurableMetricInputAppend
{
    public DurableMetricInputAppend(
        MetricInputStreamId streamId,
        DurableMetricInputFact fact,
        ShiftOccurrenceId shiftOccurrenceId,
        ProductionDayId productionDayId)
    {
        ArgumentNullException.ThrowIfNull(streamId);
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
        Fact = fact;
        ShiftOccurrenceId = shiftOccurrenceId;
        ProductionDayId = productionDayId;
    }

    public MetricInputStreamId StreamId { get; }

    public DurableMetricInputFact Fact { get; }

    public ShiftOccurrenceId ShiftOccurrenceId { get; }

    public ProductionDayId ProductionDayId { get; }
}
