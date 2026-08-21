using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Metrics;

public sealed class GoodOverProducedMetricStrategy : IMetricCalculationStrategy
{
    public string StrategyKey => MetricStrategyKeys.GoodOverProduced;

    public MetricCalculationResult Calculate(
        MetricCalculationContext context,
        MetricPolicyDefinition policy)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(policy);

        if (!context.TryGetInput(
                MetricInputKeys.GoodQuantity,
                out var goodQuantity))
        {
            return MetricCalculationResult.Unavailable(
                context.MetricKey,
                $"Missing input '{MetricInputKeys.GoodQuantity}'.");
        }

        if (!context.TryGetInput(
                MetricInputKeys.ProducedQuantity,
                out var producedQuantity))
        {
            return MetricCalculationResult.Unavailable(
                context.MetricKey,
                $"Missing input '{MetricInputKeys.ProducedQuantity}'.");
        }

        if (producedQuantity <= 0)
        {
            return MetricCalculationResult.Unavailable(
                context.MetricKey,
                "Produced quantity must be greater than zero.");
        }

        return MetricCalculationResult.Available(
            context.MetricKey,
            goodQuantity / producedQuantity);
    }
}
