namespace FactoryConnect.Abstractions;

public sealed record ShiftMetricAggregateContribution(
    ShiftMetricAggregateKey Key,
    MetricAggregateValue Value);
