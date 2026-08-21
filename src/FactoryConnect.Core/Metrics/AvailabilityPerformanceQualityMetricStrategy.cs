using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Metrics;

public sealed class AvailabilityPerformanceQualityMetricStrategy : IMetricCalculationStrategy
{
    public string StrategyKey => MetricStrategyKeys.AvailabilityPerformanceQuality;

    public MetricCalculationResult Calculate(
        MetricCalculationContext context,
        MetricPolicyDefinition policy)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(policy);

        if (!context.TryGetInput(MetricInputKeys.Availability, out var availability))
        {
            return Missing(context, MetricInputKeys.Availability);
        }

        if (!context.TryGetInput(MetricInputKeys.Performance, out var performance))
        {
            return Missing(context, MetricInputKeys.Performance);
        }

        if (!context.TryGetInput(MetricInputKeys.Quality, out var quality))
        {
            return Missing(context, MetricInputKeys.Quality);
        }

        return MetricCalculationResult.Available(
            context.MetricKey,
            availability * performance * quality);
    }

    private static MetricCalculationResult Missing(
        MetricCalculationContext context,
        string inputKey) =>
        MetricCalculationResult.Unavailable(
            context.MetricKey,
            $"Missing input '{inputKey}'.");
}
