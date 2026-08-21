namespace FactoryConnect.Abstractions;

public sealed record MetricInputDerivationResult
{
    public required IReadOnlyDictionary<string, decimal> Inputs { get; init; }

    public bool TryGetInput(string key, out decimal value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return Inputs.TryGetValue(key, out value);
    }
}
