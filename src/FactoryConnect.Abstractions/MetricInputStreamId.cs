namespace FactoryConnect.Abstractions;

public sealed record MetricInputStreamId
{
    private const string DefaultStreamKey = "metric-inputs";

    public MetricInputStreamId(
        MachineId machineId,
        string streamKey)
    {
        if (machineId.IsEmpty)
        {
            throw new ArgumentException(
                "Machine identifier must not be empty.",
                nameof(machineId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(streamKey);

        MachineId = machineId;
        StreamKey = streamKey;
    }

    public MachineId MachineId { get; }

    public string StreamKey { get; }

    public static MetricInputStreamId ForMachine(MachineId machineId) =>
        new(machineId, DefaultStreamKey);
}
