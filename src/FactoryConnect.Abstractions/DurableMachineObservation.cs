namespace FactoryConnect.Abstractions;

public sealed record DurableMachineObservation
{
    public DurableMachineObservation(
        ObservationPosition position,
        ObservationStreamId streamId,
        ulong instanceId,
        ulong sequence,
        MachineObservation observation)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(streamId);
        ArgumentNullException.ThrowIfNull(observation);

        if (observation.MachineId != streamId.MachineId)
        {
            throw new ArgumentException(
                "The observation must belong to the durable stream machine.",
                nameof(observation));
        }

        Position = position;
        StreamId = streamId;
        InstanceId = instanceId;
        Sequence = sequence;
        Observation = observation;
    }

    public ObservationPosition Position { get; }

    public ObservationStreamId StreamId { get; }

    public ulong InstanceId { get; }

    public ulong Sequence { get; }

    public MachineObservation Observation { get; }
}
