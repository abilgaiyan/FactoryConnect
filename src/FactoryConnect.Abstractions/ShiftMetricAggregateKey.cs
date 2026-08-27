namespace FactoryConnect.Abstractions;

public sealed record ShiftMetricAggregateKey
{
    public ShiftMetricAggregateKey(
        MachineId machineId,
        ShiftOccurrenceId shiftOccurrenceId,
        string metricInputKey)
    {
        if (machineId.IsEmpty)
        {
            throw new ArgumentException(
                "Machine identifier must not be empty.",
                nameof(machineId));
        }

        ArgumentNullException.ThrowIfNull(shiftOccurrenceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(metricInputKey);

        MachineId = machineId;
        ShiftOccurrenceId = shiftOccurrenceId;
        MetricInputKey = metricInputKey;
    }

    public MachineId MachineId { get; }

    public ShiftOccurrenceId ShiftOccurrenceId { get; }

    public string MetricInputKey { get; }
}
