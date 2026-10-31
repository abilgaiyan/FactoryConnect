using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Persistence.SqlServer;
namespace FactoryConnect.Integration.Tests;

// Literal semantic-stage oracle from a91618de; SQL chain/source loading remain outside this seam.
internal static class ReferenceTimeSemanticOracle
{
    internal static async Task ValidateAsync(IReadOnlyList<PositionedMetricInputFact> inputs,
        IReadOnlyList<ProductionQuantitySourceEntry> sources,
        IReadOnlyList<ProductionReferenceTimePublicationTransition> transitions,
        IReadOnlyList<PublishedProductionReferenceTimeOutcome> outcomes, long reference,
        Func<long, CancellationToken, Task<ProductionStandardAuthorityCut>> readStandard, CancellationToken cancellationToken)
    {
            if (outcomes.Select(o => o.SourceQuantityEvidenceId).Distinct().Count() != outcomes.Count)
                throw new ContainmentHistoricalIntegrityException("Duplicate historical source outcomes.");
            // Admission publishes one R per outcome. No phantom or missing R can certify a completed cut.
            long expectedR = 1;
            foreach (var outcome in outcomes)
            {
                if (outcome.PublicationRevision.Value != expectedR++)
                    throw new MissingContainmentHistoricalAuthorityException("The historical outcome revision chain is incomplete.");
            }
            if (outcomes.Count != reference)
                throw new MissingContainmentHistoricalAuthorityException("The terminal R lacks its complete outcome history.");
            var sourcesById = sources.ToDictionary(s => s.Evidence.Id);
            var standards = new Dictionary<long, ProductionStandardAuthorityCut>();
            foreach (var transition in transitions)
            {
                var deltaSources = inputs.Where(i => i.Fact.Key == MetricInputFactKeys.PartCountIncrement &&
                        (transition.ExpectedPreviousAggregationPosition is null || i.Position > transition.ExpectedPreviousAggregationPosition) &&
                        i.Position <= transition.TargetAggregationPosition)
                    .Select(i => i.Fact.SourceQuantityEvidenceId!.Value).ToHashSet();
                var admitted = outcomes.Where(o => o.PublicationRevision.Value > transition.StartingReferenceTimeRevision.Value &&
                    o.PublicationRevision.Value <= transition.CompletedReferenceTimeRevision!.Value.Value).ToArray();
                if (admitted.Any(o => !deltaSources.Contains(o.SourceQuantityEvidenceId)))
                    throw new ContainmentHistoricalIntegrityException("An outcome is outside its exact aggregation delta.");
                if (!deltaSources.SetEquals(admitted.Select(o => o.SourceQuantityEvidenceId)))
                    throw new MissingContainmentHistoricalAuthorityException("Completed transition lacks a required produced-source outcome.");
                if (deltaSources.Count == 0)
                {
                    if (transition.ProductionStandardAuthorityRevision is not null ||
                        transition.StartingReferenceTimeRevision != transition.CompletedReferenceTimeRevision)
                        throw new ContainmentHistoricalIntegrityException("Empty transition changed reference-time authority.");
                    continue;
                }
                if (transition.ProductionStandardAuthorityRevision is not long standard)
                    throw new MissingContainmentHistoricalAuthorityException("A nonempty transition has no standard authority cut.");
                if (!standards.TryGetValue(standard, out var standardCut))
                    standards.Add(standard, standardCut = await readStandard(standard, cancellationToken));
                foreach (var published in admitted)
                {
                    var outcome = published.Resolution;
                    if (outcome.AuthorityRevision != standard || !sourcesById.TryGetValue(outcome.SourceQuantityEvidenceId, out var source))
                        throw new ContainmentHistoricalIntegrityException("Outcome contradicts its transition S or source membership.");
                    var referenced = outcome.SelectedStandardVersionId is { } selected
                        ? new[] { selected } : outcome.ConflictingStandardVersionIds;
                    if (referenced.Any(id => !standardCut.Versions.Any(v => v.VersionId == id)))
                        throw new MissingContainmentHistoricalAuthorityException("A referenced historical standard version is absent.");
                    try
                    {
                        ProductionStandardResolver.ValidateCanonicalOutcome(source.Evidence, source.ShiftOccurrenceId,
                            source.ProductionDayId, standardCut, outcome);
                    }
                    catch (InvalidOperationException error)
                    { throw new ContainmentHistoricalIntegrityException("Outcome contradicts its independently resolved historical source/standard.", error); }
                }
            }
        cancellationToken.ThrowIfCancellationRequested();
    }
}
