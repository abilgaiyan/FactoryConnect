namespace FactoryConnect.Abstractions;

public sealed record ProductionDayMetricAggregateContribution
{
    public ProductionDayMetricAggregateContribution(
        ProductionDayMetricAggregateKey key,
        MetricAggregateValue value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        Key = key;
        Value = value;
    }

    public ProductionDayMetricAggregateKey Key { get; }

    public MetricAggregateValue Value { get; }
}
