namespace FactoryConnect.Abstractions;

/// <summary>Identifies an immutable cut of published production reference-time outcomes.</summary>
public readonly record struct ProductionReferenceTimeAuthorityRevision
{
    public ProductionReferenceTimeAuthorityRevision(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Value = value;
    }

    public long Value { get; }

    public static ProductionReferenceTimeAuthorityRevision Empty => new(0);
}
