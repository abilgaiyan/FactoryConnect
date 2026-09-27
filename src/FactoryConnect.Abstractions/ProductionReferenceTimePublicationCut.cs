namespace FactoryConnect.Abstractions;

/// <summary>The exact independent FC-026 and reference-time cuts required for a component publication.</summary>
public sealed record ProductionReferenceTimePublicationCut
{
    public ProductionReferenceTimePublicationCut(
        MetricAggregationCheckpoint aggregationRevision,
        ProductionReferenceTimeAuthorityRevision referenceTimeRevision)
    {
        ArgumentNullException.ThrowIfNull(aggregationRevision);

        AggregationRevision = aggregationRevision;
        ReferenceTimeRevision = referenceTimeRevision;
    }

    public MetricAggregationCheckpoint AggregationRevision { get; }

    public ProductionReferenceTimeAuthorityRevision ReferenceTimeRevision { get; }

    public MetricAggregationProcessorId MetricAggregationProcessorId => AggregationRevision.ProcessorId;

    public MetricInputPosition MetricAggregationPosition => AggregationRevision.Position;

    public MetricInputStreamId MetricInputStreamId => AggregationRevision.StreamId;
}
