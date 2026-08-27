namespace FactoryConnect.Abstractions;

public sealed record ProductionDayMetricAggregateKey
{
    public ProductionDayMetricAggregateKey(
        MachineId machineId,
        ProductionDayId productionDayId,
        string metricInputKey)
    {
        if (machineId.IsEmpty)
        {
            throw new ArgumentException(
                "Machine identifier must not be empty.",
                nameof(machineId));
        }

        ArgumentNullException.ThrowIfNull(productionDayId);
        ArgumentException.ThrowIfNullOrWhiteSpace(metricInputKey);

        MachineId = machineId;
        ProductionDayId = productionDayId;
        MetricInputKey = metricInputKey;
    }

    public MachineId MachineId { get; }

    public ProductionDayId ProductionDayId { get; }

    public string MetricInputKey { get; }
}
