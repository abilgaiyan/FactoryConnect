using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Metrics;

public sealed class ReferenceTimeOverAptMetricStrategy : IMetricCalculationStrategy
{
    public string StrategyKey => MetricStrategyKeys.ReferenceTimeOverApt;

    public MetricCalculationResult Calculate(
        MetricCalculationContext context,
        MetricPolicyDefinition policy)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(policy);

        if (!context.TryGetInput(
                MetricInputKeys.ProductionReferenceTime,
                out var productionReferenceTime))
        {
            return MetricCalculationResult.Unavailable(
                context.MetricKey,
                $"Missing input '{MetricInputKeys.ProductionReferenceTime}'.");
        }

        if (!context.TryGetInput(
                MetricInputKeys.ActualProductionTime,
                out var actualProductionTime))
        {
            return MetricCalculationResult.Unavailable(
                context.MetricKey,
                $"Missing input '{MetricInputKeys.ActualProductionTime}'.");
        }

        if (actualProductionTime <= 0)
        {
            return MetricCalculationResult.Unavailable(
                context.MetricKey,
                "Actual production time must be greater than zero.");
        }

        return MetricCalculationResult.Available(
            context.MetricKey,
            productionReferenceTime / actualProductionTime);
    }
}
