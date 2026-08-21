namespace FactoryConnect.Abstractions;

public sealed record ProductionSchedule
{
    public required CompanyId CompanyId { get; init; }
    public required SiteId SiteId { get; init; }
    public required MachineId MachineId { get; init; }
    public required ShiftId ShiftId { get; init; }
    public required DateOnly ProductionDate { get; init; }
    public required TimeSpan PlannedOperatingTime { get; init; }
}
