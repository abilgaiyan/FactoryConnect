namespace FactoryConnect.Abstractions;

public sealed record ObservationProcessingCheckpoint
{
    public ObservationProcessingCheckpoint(
        ObservationProcessorId processorId,
        ObservationStreamId streamId,
        ObservationPosition position)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(streamId);
        ArgumentNullException.ThrowIfNull(position);

        ProcessorId = processorId;
        StreamId = streamId;
        Position = position;
    }

    public ObservationProcessorId ProcessorId { get; }

    public ObservationStreamId StreamId { get; }

    public ObservationPosition Position { get; }
}
