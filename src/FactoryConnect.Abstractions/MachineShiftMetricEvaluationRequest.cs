namespace FactoryConnect.Abstractions;

public sealed record MachineShiftMetricEvaluationRequest
{
    public required CompanyId CompanyId { get; init; }
    public required SiteId SiteId { get; init; }
    public required MachineId MachineId { get; init; }
    public required ShiftId ShiftId { get; init; }
    public required DateOnly ProductionDate { get; init; }
    public required IReadOnlyCollection<MachineActivityPeriod> ActivityPeriods { get; init; }
    public required ProductionSchedule Schedule { get; init; }
    public required IReadOnlyCollection<ProductionEntry> ProductionEntries { get; init; }
    public IReadOnlyDictionary<string, decimal> AdditionalInputs { get; init; } =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
    public required IReadOnlyCollection<MetricPolicyDefinition> MetricPolicies { get; init; }
}
