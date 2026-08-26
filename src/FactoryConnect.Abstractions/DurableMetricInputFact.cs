namespace FactoryConnect.Abstractions;

public sealed record DurableMetricInputFact
{
    public required MetricInputFactId Id { get; init; }
    public required string Key { get; init; }
    public required decimal Value { get; init; }
    public required string Unit { get; init; }
    public required DateTimeOffset StartsAtUtc { get; init; }
    public required DateTimeOffset EndsAtUtc { get; init; }
    public required CompanyId CompanyId { get; init; }
    public required SiteId SiteId { get; init; }
    public ProductionLineId? ProductionLineId { get; init; }
    public required MachineId MachineId { get; init; }
    public required ShiftId ShiftId { get; init; }
    public ProductionContextAssignmentId? ProductionContextAssignmentId { get; init; }
    public ProductionOrderId? ProductionOrderId { get; init; }
    public OperationId? OperationId { get; init; }
    public PartId? PartId { get; init; }
    public OperatorId? OperatorId { get; init; }
    public ProductionTimeEligibilityIntervalId? SourceEligibilityIntervalId { get; init; }
    public ProductionQuantityEvidenceId? SourceQuantityEvidenceId { get; init; }
}
