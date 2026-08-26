namespace FactoryConnect.Abstractions;

public sealed record ProductionContextProcessingScope
{
    public required CompanyId CompanyId { get; init; }
    public required SiteId SiteId { get; init; }
    public required ProductionLineId ProductionLineId { get; init; }
    public required MachineId MachineId { get; init; }
    public required ObservationStreamId StreamId { get; init; }

    public void Validate()
    {
        if (CompanyId.IsEmpty)
        {
            throw new ArgumentException("Company ID is required.", nameof(CompanyId));
        }

        if (SiteId.IsEmpty)
        {
            throw new ArgumentException("Site ID is required.", nameof(SiteId));
        }

        if (ProductionLineId.IsEmpty)
        {
            throw new ArgumentException("Production line ID is required.", nameof(ProductionLineId));
        }

        if (MachineId.IsEmpty)
        {
            throw new ArgumentException("Machine ID is required.", nameof(MachineId));
        }

        ArgumentNullException.ThrowIfNull(StreamId);
        if (StreamId.MachineId != MachineId)
        {
            throw new ArgumentException("Stream machine must match processing scope machine.", nameof(StreamId));
        }
    }
}
