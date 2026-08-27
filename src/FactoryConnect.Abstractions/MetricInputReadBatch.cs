namespace FactoryConnect.Abstractions;

public sealed record MetricInputReadBatch
{
    public MetricInputReadBatch(
        MetricInputStreamId streamId,
        MetricInputPosition? afterPosition,
        MetricInputPosition? throughPosition,
        IReadOnlyList<PositionedMetricInputFact> facts)
    {
        ArgumentNullException.ThrowIfNull(streamId);
        ArgumentNullException.ThrowIfNull(facts);

        if (afterPosition is not null &&
            throughPosition is not null &&
            throughPosition < afterPosition)
        {
            throw new ArgumentException(
                "Read-through position must not precede the requested after position.",
                nameof(throughPosition));
        }

        var snapshot = facts.ToArray();

        if (throughPosition is null && snapshot.Length != 0)
        {
            throw new ArgumentException(
                "A batch without source progress cannot contain metric input facts.",
                nameof(facts));
        }

        var factIds = new HashSet<MetricInputFactId>();

        for (var index = 0; index < snapshot.Length; index++)
        {
            var item = snapshot[index];

            if (item.StreamId != streamId)
            {
                throw new ArgumentException(
                    "Every positioned metric input fact must belong to the requested stream.",
                    nameof(facts));
            }

            if (!factIds.Add(item.Fact.Id))
            {
                throw new ArgumentException(
                    "A metric input fact identity must not appear at multiple positions in one read batch.",
                    nameof(facts));
            }

            if (afterPosition is not null && item.Position <= afterPosition)
            {
                throw new ArgumentException(
                    "Metric input fact positions must follow the requested after position.",
                    nameof(facts));
            }

            if (throughPosition is not null && item.Position > throughPosition)
            {
                throw new ArgumentException(
                    "Metric input fact positions must not exceed the read-through position.",
                    nameof(facts));
            }

            if (index > 0 && snapshot[index - 1].Position >= item.Position)
            {
                throw new ArgumentException(
                    "Metric input facts must be ordered by strictly increasing position.",
                    nameof(facts));
            }
        }

        StreamId = streamId;
        AfterPosition = afterPosition;
        ThroughPosition = throughPosition;
        Facts = snapshot;
    }

    public MetricInputStreamId StreamId { get; }

    public MetricInputPosition? AfterPosition { get; }

    public MetricInputPosition? ThroughPosition { get; }

    public IReadOnlyList<PositionedMetricInputFact> Facts { get; }
}
