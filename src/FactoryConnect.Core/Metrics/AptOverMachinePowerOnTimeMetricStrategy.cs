using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Metrics;

public sealed class AptOverMachinePowerOnTimeMetricStrategy : IMetricCalculationStrategy
{
    public string StrategyKey => MetricStrategyKeys.AptOverMachinePowerOnTime;

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
                MetricInputKeys.MachinePowerOnTime,
                out var machinePowerOnTime))
        {
            return MetricCalculationResult.Unavailable(
                context.MetricKey,
                $"Missing input '{MetricInputKeys.MachinePowerOnTime}'.");
        }

        if (machinePowerOnTime <= 0)
        {
            return MetricCalculationResult.Unavailable(
                context.MetricKey,
                "Machine power-on time must be greater than zero.");
        }

        return MetricCalculationResult.Available(
            context.MetricKey,
            actualProductionTime / machinePowerOnTime);
    }
}
