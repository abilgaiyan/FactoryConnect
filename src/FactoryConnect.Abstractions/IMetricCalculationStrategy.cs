namespace FactoryConnect.Abstractions;

public interface IMetricCalculationStrategy
{
    string StrategyKey { get; }

    MetricCalculationResult Calculate(
        MetricCalculationContext context,
        MetricPolicyDefinition policy);
}
