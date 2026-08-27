namespace FactoryConnect.Abstractions;

public readonly record struct ContextualizedActivityIntervalId(string Value)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);

    public override string ToString() => Value;
}
