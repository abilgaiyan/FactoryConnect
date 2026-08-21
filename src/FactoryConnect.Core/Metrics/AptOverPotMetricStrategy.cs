using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Metrics;

public sealed class AptOverPotMetricStrategy : IMetricCalculationStrategy
{
    public string StrategyKey => MetricStrategyKeys.AptOverPot;

    public MetricCalculationResult Calculate(
        MetricCalculationContext context,
        MetricPolicyDefinition policy)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(policy);

        if (!context.TryGetInput(
                MetricInputKeys.ActualProductionTime,
                out var actualProductionTime))
        {
            return MetricCalculationResult.Unavailable(
                context.MetricKey,
                $"Missing input '{MetricInputKeys.ActualProductionTime}'.");
        }

        if (!context.TryGetInput(
                MetricInputKeys.PlannedOperatingTime,
                out var plannedOperatingTime))
        {
            return MetricCalculationResult.Unavailable(
                context.MetricKey,
                $"Missing input '{MetricInputKeys.PlannedOperatingTime}'.");
        }

        if (plannedOperatingTime <= 0)
        {
            return MetricCalculationResult.Unavailable(
                context.MetricKey,
                "Planned operating time must be greater than zero.");
        }

        return MetricCalculationResult.Available(
            context.MetricKey,
            actualProductionTime / plannedOperatingTime);
    }
}
