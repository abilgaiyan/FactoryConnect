namespace FactoryConnect.Abstractions;

public sealed record MetricInputReadRequest
{
    public MetricInputReadRequest(
        MetricInputStreamId streamId,
        MetricInputPosition? afterPosition,
        int maxCount)
    {
        ArgumentNullException.ThrowIfNull(streamId);

        if (maxCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxCount),
                "Maximum metric input count must be greater than zero.");
        }

        StreamId = streamId;
        AfterPosition = afterPosition;
        MaxCount = maxCount;
    }

    public MetricInputStreamId StreamId { get; }

    public MetricInputPosition? AfterPosition { get; }

    public int MaxCount { get; }

    public static MetricInputReadRequest FromCheckpoint(
        MetricAggregationProcessorId processorId,
        MetricInputStreamId streamId,
        MetricAggregationCheckpoint? checkpoint,
        int maxCount)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(streamId);

        if (checkpoint is not null && checkpoint.ProcessorId != processorId)
        {
            throw new ArgumentException(
                "Aggregation checkpoint must belong to the requested processor.",
                nameof(checkpoint));
        }

        if (checkpoint is not null && checkpoint.StreamId != streamId)
        {
            throw new ArgumentException(
                "Aggregation checkpoint must belong to the requested metric input stream.",
                nameof(checkpoint));
        }

        return new MetricInputReadRequest(
            streamId,
            checkpoint?.Position,
            maxCount);
    }
}
