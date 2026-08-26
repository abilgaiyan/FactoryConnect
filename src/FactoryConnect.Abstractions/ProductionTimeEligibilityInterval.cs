namespace FactoryConnect.Abstractions;

public sealed record ProductionTimeEligibilityInterval
{
    public required ProductionTimeEligibilityIntervalId Id { get; init; }
    public required ContextualizedActivityIntervalId SourceContextualizedActivityIntervalId { get; init; }
    public required CompanyId CompanyId { get; init; }
    public required SiteId SiteId { get; init; }
    public ProductionLineId? ProductionLineId { get; init; }
    public required MachineId MachineId { get; init; }
    public required MachineState State { get; init; }
    public required ShiftId ShiftId { get; init; }
    public required ShiftScheduleAssignmentId ShiftScheduleAssignmentId { get; init; }
    public ProductionContextAssignmentId? ProductionContextAssignmentId { get; init; }
    public ProductionOrderId? ProductionOrderId { get; init; }
    public OperationId? OperationId { get; init; }
    public PartId? PartId { get; init; }
    public OperatorId? OperatorId { get; init; }
    public required DateTimeOffset StartsAtUtc { get; init; }
    public required DateTimeOffset EndsAtUtc { get; init; }
    public required bool IsPlannedProductionTime { get; init; }
    public PlannedProductionScheduleAssignmentId? PlannedProductionScheduleAssignmentId { get; init; }

    public TimeSpan Duration => EndsAtUtc - StartsAtUtc;
}
