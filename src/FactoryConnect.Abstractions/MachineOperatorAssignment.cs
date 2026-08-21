namespace FactoryConnect.Abstractions;

public sealed record MachineOperatorAssignment
{
    public required CompanyId CompanyId { get; init; }
    public required SiteId SiteId { get; init; }
    public required MachineId MachineId { get; init; }
    public required ShiftId ShiftId { get; init; }
    public required OperatorId OperatorId { get; init; }
    public required DateTimeOffset StartsAt { get; init; }
    public DateTimeOffset? EndsAt { get; init; }
}
