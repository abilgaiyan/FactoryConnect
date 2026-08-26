namespace FactoryConnect.Abstractions;

public sealed record ProductionContextProcessingCommit
{
    public required ObservationProcessingCheckpoint? ExpectedCheckpoint { get; init; }
    public required ObservationProcessingCheckpoint NextCheckpoint { get; init; }
    public IReadOnlyList<ContextualizedActivityInterval> ContextualizedActivity { get; init; } = [];
    public IReadOnlyList<ProductionTimeEligibilityInterval> EligibilityIntervals { get; init; } = [];
    public IReadOnlyList<DurableMetricInputFact> MetricFacts { get; init; } = [];
}
