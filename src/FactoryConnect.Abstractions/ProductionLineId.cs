namespace FactoryConnect.Abstractions;

public readonly record struct ProductionLineId(string Value)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);

    public override string ToString() => Value;
}
