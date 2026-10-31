using FactoryConnect.Abstractions;

namespace FactoryConnect.Persistence.SqlServer;

// Call only after the existing complete A/R-chain and outcome-order checks. Source sorting
// permits arbitrary input enumeration; outcomes are deliberately NOT sorted or repaired.
internal sealed class ReferenceTimeTransitionPartitioner
{
    private readonly PositionedMetricInputFact[] _produced;
    private readonly IReadOnlyList<PublishedProductionReferenceTimeOutcome> _outcomes;
    private int _factIndex;
    private int _outcomeIndex;

    internal ReferenceTimeTransitionPartitioner(IReadOnlyList<PositionedMetricInputFact> inputs,
        IReadOnlyList<PublishedProductionReferenceTimeOutcome> outcomes)
    {
        var produced = new List<PositionedMetricInputFact>();
        foreach (var input in inputs)
        {
            FactVisits++;
            if (input.Fact.Key == MetricInputFactKeys.PartCountIncrement) produced.Add(input);
        }
        _produced = produced.OrderBy(static item => item.Position).ToArray();
        _outcomes = outcomes;
    }

    // Structural visits exclude sorting comparisons, which remain included in elapsed/allocation measurements.
    internal long FactVisits { get; private set; }
    internal long OutcomeVisits { get; private set; }

    internal (HashSet<ProductionQuantityEvidenceId> Sources, PublishedProductionReferenceTimeOutcome[] Outcomes) Next(
        ProductionReferenceTimePublicationTransition transition)
    {
        HashSet<ProductionQuantityEvidenceId> sources = [];
        while (_factIndex < _produced.Length)
        {
            FactVisits++;
            var input = _produced[_factIndex];
            if (input.Position > transition.TargetAggregationPosition) break;
            _factIndex++;
            if (transition.ExpectedPreviousAggregationPosition is null || input.Position > transition.ExpectedPreviousAggregationPosition)
                sources.Add(input.Fact.SourceQuantityEvidenceId!.Value);
        }

        var admitted = new List<PublishedProductionReferenceTimeOutcome>();
        while (_outcomeIndex < _outcomes.Count)
        {
            OutcomeVisits++;
            var outcome = _outcomes[_outcomeIndex];
            if (outcome.PublicationRevision.Value > transition.CompletedReferenceTimeRevision!.Value.Value) break;
            _outcomeIndex++;
            if (outcome.PublicationRevision.Value > transition.StartingReferenceTimeRevision.Value) admitted.Add(outcome);
        }
        return (sources, admitted.ToArray());
    }
}
