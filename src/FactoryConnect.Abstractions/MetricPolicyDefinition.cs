namespace FactoryConnect.Abstractions;

public sealed record MetricPolicyDefinition
{
    public required string MetricKey { get; init; }
    public required string StrategyKey { get; init; }
    public IReadOnlyDictionary<string, string> Parameters { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
