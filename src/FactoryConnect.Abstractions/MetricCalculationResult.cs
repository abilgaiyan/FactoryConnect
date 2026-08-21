namespace FactoryConnect.Abstractions;

public sealed record MetricCalculationResult
{
    public required string MetricKey { get; init; }
    public decimal? Value { get; init; }
    public required bool IsAvailable { get; init; }
    public string? Reason { get; init; }

    public static MetricCalculationResult Available(
        string metricKey,
        decimal value) =>
        new()
        {
            MetricKey = metricKey,
            Value = value,
            IsAvailable = true,
        };

    public static MetricCalculationResult Unavailable(
        string metricKey,
        string reason) =>
        new()
        {
            MetricKey = metricKey,
            IsAvailable = false,
            Reason = reason,
        };
}
