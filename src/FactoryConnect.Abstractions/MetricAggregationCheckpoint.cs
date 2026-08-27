namespace FactoryConnect.Abstractions;

public sealed record MetricAggregationCheckpoint
{
    public MetricAggregationCheckpoint(
        MetricAggregationProcessorId processorId,
        MetricInputStreamId streamId,
        MetricInputPosition position)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(streamId);
        ArgumentNullException.ThrowIfNull(position);

        ProcessorId = processorId;
        StreamId = streamId;
        Position = position;
    }

    public MetricAggregationProcessorId ProcessorId { get; }

    public MetricInputStreamId StreamId { get; }

    public MetricInputPosition Position { get; }
}
