namespace FactoryConnect.Abstractions;

public sealed record MachineShiftMetricResult
{
    public required CompanyId CompanyId { get; init; }
    public required SiteId SiteId { get; init; }
    public required MachineId MachineId { get; init; }
    public required ShiftId ShiftId { get; init; }
    public required DateOnly ProductionDate { get; init; }
    public required IReadOnlyDictionary<string, decimal> Inputs { get; init; }
    public required IReadOnlyDictionary<string, MetricCalculationResult> Metrics { get; init; }
}
