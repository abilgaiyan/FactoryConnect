namespace FactoryConnect.Abstractions;

public sealed record MetricAggregationCommit
{
    public MetricAggregationCommit(
        MetricAggregationProcessorId processorId,
        MetricAggregationCheckpoint? expectedCheckpoint,
        MetricAggregationCheckpoint proposedCheckpoint,
        IReadOnlyList<PositionedMetricInputFact> inputs)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(proposedCheckpoint);
        ArgumentNullException.ThrowIfNull(inputs);

        if (proposedCheckpoint.ProcessorId != processorId)
        {
            throw new ArgumentException(
                "Proposed aggregation checkpoint must belong to the committing processor.",
                nameof(proposedCheckpoint));
        }

        if (expectedCheckpoint is not null &&
            (expectedCheckpoint.ProcessorId != processorId ||
             expectedCheckpoint.StreamId != proposedCheckpoint.StreamId))
        {
            throw new ArgumentException(
                "Expected aggregation checkpoint must belong to the same processor and metric input stream.",
                nameof(expectedCheckpoint));
        }

        if (expectedCheckpoint is not null &&
            proposedCheckpoint.Position <= expectedCheckpoint.Position)
        {
            throw new ArgumentException(
                "Proposed aggregation checkpoint must advance beyond the expected checkpoint.",
                nameof(proposedCheckpoint));
        }

        var snapshot = inputs.ToArray();
        if (snapshot.Any(static input => input is null))
        {
            throw new ArgumentException(
                "Aggregation commit inputs must not contain null items.",
                nameof(inputs));
        }

        if (snapshot.Any(input => input.StreamId != proposedCheckpoint.StreamId))
        {
            throw new ArgumentException(
                "Aggregation commit inputs must belong to the proposed checkpoint stream.",
                nameof(inputs));
        }

        ProcessorId = processorId;
        ExpectedCheckpoint = expectedCheckpoint;
        ProposedCheckpoint = proposedCheckpoint;
        Inputs = snapshot;
    }

    public MetricAggregationProcessorId ProcessorId { get; }

    public MetricAggregationCheckpoint? ExpectedCheckpoint { get; }

    public MetricAggregationCheckpoint ProposedCheckpoint { get; }

    public IReadOnlyList<PositionedMetricInputFact> Inputs { get; }
}
