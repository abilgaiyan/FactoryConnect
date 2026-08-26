namespace FactoryConnect.Abstractions;

public readonly record struct ProductionQuantityEvidenceId(string Value)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);

    public override string ToString() => Value;
}
