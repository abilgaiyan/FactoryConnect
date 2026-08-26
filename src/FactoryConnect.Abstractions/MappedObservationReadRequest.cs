namespace FactoryConnect.Abstractions;

public sealed record MappedObservationReadRequest
{
    public MappedObservationReadRequest(
        ObservationStreamId streamId,
        ObservationPosition? afterPosition,
        int batchSize)
    {
        ArgumentNullException.ThrowIfNull(streamId);

        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(batchSize),
                "Batch size must be greater than zero.");
        }

        StreamId = streamId;
        AfterPosition = afterPosition;
        BatchSize = batchSize;
    }

    public ObservationStreamId StreamId { get; }

    public ObservationPosition? AfterPosition { get; }

    public int BatchSize { get; }
}
