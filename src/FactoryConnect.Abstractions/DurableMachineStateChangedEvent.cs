namespace FactoryConnect.Abstractions;

public sealed record DurableMachineStateChangedEvent
{
    public DurableMachineStateChangedEvent(
        ObservationProcessorId processorId,
        ObservationPosition position,
        ObservationStreamId streamId,
        ulong instanceId,
        ulong sequence,
        MachineStateChangedEvent stateChanged)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(streamId);
        ArgumentNullException.ThrowIfNull(stateChanged);

        if (stateChanged.MachineId != streamId.MachineId)
        {
            throw new ArgumentException(
                "The state change must belong to the durable stream machine.",
                nameof(stateChanged));
        }

        ProcessorId = processorId;
        Position = position;
        StreamId = streamId;
        InstanceId = instanceId;
        Sequence = sequence;
        StateChanged = stateChanged;
    }

    public ObservationProcessorId ProcessorId { get; }

    public ObservationPosition Position { get; }

    public ObservationStreamId StreamId { get; }

    public ulong InstanceId { get; }

    public ulong Sequence { get; }

    public MachineStateChangedEvent StateChanged { get; }
}
