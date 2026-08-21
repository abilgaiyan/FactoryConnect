namespace FactoryConnect.Abstractions;

public sealed record MetricCalculationContext
{
    public required string MetricKey { get; init; }

    public IReadOnlyDictionary<string, decimal> Inputs { get; init; } =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

    public bool TryGetInput(string key, out decimal value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return Inputs.TryGetValue(key, out value);
    }
}
