namespace FactoryConnect.Abstractions;

public sealed record ObservationCheckpoint
{
    public ObservationCheckpoint(
        ObservationStreamId streamId,
        ulong instanceId,
        ulong nextSequence)
    {
        ArgumentNullException.ThrowIfNull(streamId);

        StreamId = streamId;
        InstanceId = instanceId;
        NextSequence = nextSequence;
    }

    public ObservationStreamId StreamId { get; }

    public ulong InstanceId { get; }

    public ulong NextSequence { get; }
}
