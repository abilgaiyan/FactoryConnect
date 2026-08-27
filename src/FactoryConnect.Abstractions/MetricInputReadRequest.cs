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
}
