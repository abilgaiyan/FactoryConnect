namespace FactoryConnect.Abstractions;

public sealed record ObservationStreamId
{
    public ObservationStreamId(
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
}
