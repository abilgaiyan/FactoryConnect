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

public sealed record MachineShiftRosterMaterializationRequest
{
    public MachineShiftRosterMaterializationRequest(
        DateOnly fromProductionDayInclusive,
        DateOnly toProductionDayExclusive)
    {
        if (fromProductionDayInclusive == default)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fromProductionDayInclusive),
                "Roster materialization start production day is required.");
        }

        if (toProductionDayExclusive == default)
        {
            throw new ArgumentOutOfRangeException(
                nameof(toProductionDayExclusive),
                "Roster materialization exclusive end production day is required.");
        }

        if (toProductionDayExclusive <= fromProductionDayInclusive)
        {
            throw new ArgumentException(
                "Roster materialization exclusive end must be after its start.",
                nameof(toProductionDayExclusive));
        }

        FromProductionDayInclusive = fromProductionDayInclusive;
        ToProductionDayExclusive = toProductionDayExclusive;
    }

    public DateOnly FromProductionDayInclusive { get; }

    public DateOnly ToProductionDayExclusive { get; }
}
