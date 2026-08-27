namespace FactoryConnect.Abstractions;

public sealed record MetricAggregateContributionSet(
    IReadOnlyList<ShiftMetricAggregateContribution> ShiftContributions,
    IReadOnlyList<ProductionDayMetricAggregateContribution> ProductionDayContributions);
