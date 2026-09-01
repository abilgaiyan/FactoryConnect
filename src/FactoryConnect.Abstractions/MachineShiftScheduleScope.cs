namespace FactoryConnect.Abstractions;

public sealed record MachineShiftScheduleScope
{
    public MachineShiftScheduleScope(
        MachineId machineId,
        SiteId siteId,
        ProductionLineId productionLineId)
    {
        if (machineId.IsEmpty)
        {
            throw new ArgumentException("Machine ID is required.", nameof(machineId));
        }

        if (siteId.IsEmpty)
        {
            throw new ArgumentException("Site ID is required.", nameof(siteId));
        }

        if (productionLineId.IsEmpty)
        {
            throw new ArgumentException(
                "Production line ID is required.",
                nameof(productionLineId));
        }

        MachineId = machineId;
        SiteId = siteId;
        ProductionLineId = productionLineId;
    }

    public MachineId MachineId { get; }

    public SiteId SiteId { get; }

    public ProductionLineId ProductionLineId { get; }
}
