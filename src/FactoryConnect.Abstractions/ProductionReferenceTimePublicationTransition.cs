namespace FactoryConnect.Abstractions;

/// <summary>
/// Durable authority for one exact aggregation-to-reference-time publication transition.
/// A pending non-empty transition already owns one exact production-standard revision and
/// one starting reference-time revision before any source outcome is admitted. Completion
/// is a separate, forward-only state transition after exact source coverage is proven.
/// </summary>
public sealed record ProductionReferenceTimePublicationTransition
{
    public ProductionReferenceTimePublicationTransition(
        MetricAggregationProcessorId processorId,
        MetricInputPosition targetAggregationPosition,
        MetricInputPosition? expectedPreviousAggregationPosition,
        long? productionStandardAuthorityRevision,
        ProductionReferenceTimeAuthorityRevision startingReferenceTimeRevision,
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
                "A completed transition must have a final reference-time revision and a pending transition must not.",
                nameof(completedReferenceTimeRevision));
        }

        if (completedReferenceTimeRevision is { } finalRevision)
        {
            if (productionStandardAuthorityRevision is null && finalRevision != startingReferenceTimeRevision)
            {
                throw new ArgumentException(
                    "An empty completed transition must retain its starting reference-time revision.",
                    nameof(completedReferenceTimeRevision));
            }

            if (productionStandardAuthorityRevision is not null && finalRevision.Value <= startingReferenceTimeRevision.Value)
            {
                throw new ArgumentException(
                    "A non-empty completed transition must advance beyond its starting reference-time revision.",
                    nameof(completedReferenceTimeRevision));
            }
        }

        ProcessorId = processorId;
        TargetAggregationPosition = targetAggregationPosition;
        ExpectedPreviousAggregationPosition = expectedPreviousAggregationPosition;
        ProductionStandardAuthorityRevision = productionStandardAuthorityRevision;
        StartingReferenceTimeRevision = startingReferenceTimeRevision;
        CompletedReferenceTimeRevision = completedReferenceTimeRevision;
        IsCompleted = isCompleted;
    }

    public MetricAggregationProcessorId ProcessorId { get; init; }

    public MetricInputPosition TargetAggregationPosition { get; init; }

    public MetricInputPosition? ExpectedPreviousAggregationPosition { get; init; }

    /// <summary>
    /// Exact standard authority owned by this transition. Null is valid only for an
    /// empty delta; non-empty transitions establish this value before first admission.
    /// </summary>
    public long? ProductionStandardAuthorityRevision { get; init; }

    public ProductionReferenceTimeAuthorityRevision StartingReferenceTimeRevision { get; init; }

    public ProductionReferenceTimeAuthorityRevision? CompletedReferenceTimeRevision { get; init; }

    public bool IsCompleted { get; init; }
}
