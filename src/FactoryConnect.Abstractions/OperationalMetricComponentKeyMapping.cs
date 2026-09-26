namespace FactoryConnect.Abstractions;

/// <summary>Maps FC-027 operand keys to the durable FC-026 aggregate keys without changing either contract.</summary>
public static class OperationalMetricComponentKeyMapping
{
    public static string ComponentKey(string aggregateKey) => aggregateKey switch
    {
        MetricInputFactKeys.RunningDuration => MetricInputKeys.ActualProductionTime,
        MetricInputFactKeys.PlannedProductionDuration => MetricInputKeys.PlannedOperatingTime,
        MetricInputFactKeys.ScheduledDuration => MetricInputKeys.MachinePowerOnTime,
        MetricInputFactKeys.PartCountIncrement => MetricInputKeys.ProducedQuantity,
        MetricInputFactKeys.GoodQuantity => MetricInputKeys.GoodQuantity,
        _ => aggregateKey,
    };

    public static string AggregateKey(string componentKey) => componentKey switch
    {
        MetricInputKeys.ActualProductionTime => MetricInputFactKeys.RunningDuration,
        MetricInputKeys.PlannedOperatingTime => MetricInputFactKeys.PlannedProductionDuration,
        MetricInputKeys.MachinePowerOnTime => MetricInputFactKeys.ScheduledDuration,
        MetricInputKeys.ProducedQuantity => MetricInputFactKeys.PartCountIncrement,
        MetricInputKeys.GoodQuantity => MetricInputFactKeys.GoodQuantity,
        _ => componentKey,
    };

    public static bool Matches(string componentKey, string aggregateKey) =>
        string.Equals(aggregateKey, AggregateKey(componentKey), StringComparison.Ordinal) ||
        string.Equals(aggregateKey, componentKey, StringComparison.Ordinal);
}
