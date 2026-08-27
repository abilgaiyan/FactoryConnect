namespace FactoryConnect.Abstractions;

public sealed record ProductionDayMetricAggregateContribution(
    ProductionDayMetricAggregateKey Key,
    MetricAggregateValue Value);
