using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Metrics;

public sealed class MetricCalculationEngine
{
    private readonly Dictionary<string, IMetricCalculationStrategy> _strategies;

    public MetricCalculationEngine(
        IEnumerable<IMetricCalculationStrategy> strategies)
    {
        ArgumentNullException.ThrowIfNull(strategies);

        _strategies = strategies.ToDictionary(
            strategy => strategy.StrategyKey,
            StringComparer.OrdinalIgnoreCase);
    }

    public MetricCalculationResult Calculate(
        MetricCalculationContext context,
        MetricPolicyDefinition policy)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(policy);

        if (!string.Equals(
                context.MetricKey,
                policy.MetricKey,
                StringComparison.OrdinalIgnoreCase))
        {
            return MetricCalculationResult.Unavailable(
                context.MetricKey,
                $"Metric policy '{policy.MetricKey}' does not match calculation context '{context.MetricKey}'.");
        }

        if (!_strategies.TryGetValue(policy.StrategyKey, out var strategy))
        {
            return MetricCalculationResult.Unavailable(
                context.MetricKey,
                $"Metric strategy '{policy.StrategyKey}' is not registered.");
        }

        return strategy.Calculate(context, policy);
    }
}
