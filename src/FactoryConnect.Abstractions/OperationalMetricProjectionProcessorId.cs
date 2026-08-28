namespace FactoryConnect.Abstractions;

public sealed record OperationalMetricProjectionProcessorId
{
    public OperationalMetricProjectionProcessorId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
