namespace FactoryConnect.Abstractions;

/// <summary>
/// Durable authority for one exact aggregation-to-reference-time publication transition.
/// A pending transition may establish one production-standard revision when its first
/// source outcome is admitted. Completion is separate from source admission so a
/// multi-source aggregation cut cannot become visible after only a prefix is durable.
/// </summary>
public sealed record ProductionReferenceTimePublicationTransition
{
    public ProductionReferenceTimePublicationTransition(
        MetricAggregationProcessorId processorId,
        MetricInputPosition targetAggregationPosition,
        MetricInputPosition? expectedPreviousAggregationPosition,
        long? productionStandardAuthorityRevision,
        ProductionReferenceTimeAuthorityRevision? completedReferenceTimeRevision,
        bool isCompleted)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(targetAggregationPosition);

        if (expectedPreviousAggregationPosition is not null &&
            expectedPreviousAggregationPosition >= targetAggregationPosition)
        {
            throw new ArgumentException(
                "The expected previous aggregation position must precede the target position.",
                nameof(expectedPreviousAggregationPosition));
        }

        if (productionStandardAuthorityRevision is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(productionStandardAuthorityRevision),
                "Production-standard authority revision cannot be negative.");
        }

        if (isCompleted != (completedReferenceTimeRevision is not null))
        {
            throw new ArgumentException(
                "A completed transition must have a completed reference-time revision and a pending transition must not.",
                nameof(completedReferenceTimeRevision));
        }

        ProcessorId = processorId;
        TargetAggregationPosition = targetAggregationPosition;
        ExpectedPreviousAggregationPosition = expectedPreviousAggregationPosition;
        ProductionStandardAuthorityRevision = productionStandardAuthorityRevision;
        CompletedReferenceTimeRevision = completedReferenceTimeRevision;
        IsCompleted = isCompleted;
    }

    public MetricAggregationProcessorId ProcessorId { get; }

    public MetricInputPosition TargetAggregationPosition { get; }

    public MetricInputPosition? ExpectedPreviousAggregationPosition { get; }

    /// <summary>
    /// Exact standard authority established by the first durable outcome in the batch.
    /// Null is valid before admission and for an empty completed delta.
    /// </summary>
    public long? ProductionStandardAuthorityRevision { get; }

    public ProductionReferenceTimeAuthorityRevision? CompletedReferenceTimeRevision { get; }

    public bool IsCompleted { get; }
}
