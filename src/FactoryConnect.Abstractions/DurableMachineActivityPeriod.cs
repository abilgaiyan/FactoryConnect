namespace FactoryConnect.Abstractions;

public sealed record DurableMachineActivityPeriod
{
    public DurableMachineActivityPeriod(
        ObservationProcessorId processorId,
        ObservationPosition position,
        ObservationStreamId streamId,
        ulong instanceId,
        ulong sequence,
        MachineActivityPeriod period)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(streamId);
        ArgumentNullException.ThrowIfNull(period);

        if (period.MachineId != streamId.MachineId)
        {
            throw new ArgumentException(
                "The activity period must belong to the durable stream machine.",
                nameof(period));
        }

        ProcessorId = processorId;
        Position = position;
        StreamId = streamId;
        InstanceId = instanceId;
        Sequence = sequence;
        Period = period;
    }

    public ObservationProcessorId ProcessorId { get; }

    public ObservationPosition Position { get; }

    public ObservationStreamId StreamId { get; }

    public ulong InstanceId { get; }

    public ulong Sequence { get; }

    public MachineActivityPeriod Period { get; }
}
