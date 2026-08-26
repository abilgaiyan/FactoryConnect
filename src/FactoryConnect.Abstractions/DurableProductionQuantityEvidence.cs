namespace FactoryConnect.Abstractions;

public sealed record DurableProductionQuantityEvidence
{
    public DurableProductionQuantityEvidence(
        ObservationPosition position,
        ObservationStreamId streamId,
        ProductionQuantityEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(streamId);
        ArgumentNullException.ThrowIfNull(evidence);

        if (evidence.MachineId != streamId.MachineId)
        {
            throw new ArgumentException(
                "Quantity evidence must belong to the durable stream machine.",
                nameof(evidence));
        }

        Position = position;
        StreamId = streamId;
        Evidence = evidence;
    }

    public ObservationPosition Position { get; }

    public ObservationStreamId StreamId { get; }

    public ProductionQuantityEvidence Evidence { get; }
}
