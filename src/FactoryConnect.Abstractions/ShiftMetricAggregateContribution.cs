namespace FactoryConnect.Abstractions;

public sealed record ShiftMetricAggregateContribution
{
    public ShiftMetricAggregateContribution(
        ShiftMetricAggregateKey key,
        MetricAggregateValue value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        Key = key;
        Value = value;
    }

    public ShiftMetricAggregateKey Key { get; }

    public MetricAggregateValue Value { get; }
}
