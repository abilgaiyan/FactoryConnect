namespace FactoryConnect.Abstractions;

public readonly record struct MetricInputFactId(string Value)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);

    public override string ToString() => Value;
}
