namespace FactoryConnect.Abstractions;

public readonly record struct ConfigurationVersion(string Value)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);

    public override string ToString() => Value;
}
