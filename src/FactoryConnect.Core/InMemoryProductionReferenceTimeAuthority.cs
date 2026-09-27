using FactoryConnect.Abstractions;

namespace FactoryConnect.Core;

/// <summary>Owns derived outcomes and reconciles them with the FC-026 inventory at exact authority cuts.</summary>
public sealed class InMemoryProductionReferenceTimeAuthority
{
    private readonly object _sync = new();
    private readonly Dictionary<ProductionQuantityEvidenceId, PublishedOutcome> _outcomes = [];
    private long _revision;

    public ProductionReferenceTimeAuthorityRevision CurrentRevision
    {
        get
        {
            lock (_sync)
            {
                return new ProductionReferenceTimeAuthorityRevision(_revision);
            }
        }
    }

    public ProductionReferenceTimeResolution ResolveAndRecord(
        ProductionQuantityEvidence evidence,
        ShiftOccurrenceId shift,
        ProductionDayId day,
        ProductionStandardAuthorityCut cut)
    {
        var outcome = ProductionStandardResolver.Resolve(evidence, shift, day, cut);
        lock (_sync)
        {
            if (_outcomes.TryGetValue(outcome.SourceQuantityEvidenceId, out var published))
            {
                var existing = published.Outcome;
                if (existing with { ConflictingStandardVersionIds = outcome.ConflictingStandardVersionIds } != outcome ||
                    !existing.ConflictingStandardVersionIds.SequenceEqual(
                        outcome.ConflictingStandardVersionIds, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Ordinary replay cannot change a reference-time source outcome or its authority cut.");
                }

                return existing;
            }

            var publicationRevision = checked(_revision + 1);
            _outcomes.Add(
                outcome.SourceQuantityEvidenceId,
                new PublishedOutcome(outcome, new ProductionReferenceTimeAuthorityRevision(publicationRevision)));
            _revision = publicationRevision;
            return outcome;
        }
    }

    public bool IsCompleteAtRevision(
        InMemoryMetricAggregationStore aggregationStore,
        MetricAggregationCheckpoint aggregationRevision,
        ProductionReferenceTimeAuthorityRevision referenceTimeRevision,
        OperationalMetricPeriodId periodId)
    {
        ArgumentNullException.ThrowIfNull(aggregationStore);
        ArgumentNullException.ThrowIfNull(aggregationRevision);
        ArgumentNullException.ThrowIfNull(periodId);

        // Validate the requested reference-time cut before inspecting period inventory so an
        // unavailable authority request cannot be hidden by an empty aggregation period.
        lock (_sync)
        {
            if (referenceTimeRevision.Value > _revision)
            {
                throw new InvalidOperationException("Reference-time authority revision is not available.");
            }
        }

        // FC-026 owns the produced inventory at its exact cut. This authority independently owns
        // outcome visibility at the supplied reference-time cut. The pair is the publication identity.
        var produced = aggregationStore.ReadProducedQuantityAtRevision(aggregationRevision, periodId);
        if (produced.Count == 0)
        {
            return false;
        }

        var sources = new List<ProductionQuantityEvidenceId>(produced.Count);
        var outcomes = new List<ProductionReferenceTimeResolution>(produced.Count);
        lock (_sync)
        {
            foreach (var input in produced)
            {
                var fact = input.Fact;
                if (fact.SourceQuantityEvidenceId is not { } sourceId || sourceId.IsEmpty ||
                    fact.Unit != MetricInputFactUnits.Count || fact.Value < 0 ||
                    fact.Value != decimal.Truncate(fact.Value) || fact.Value > int.MaxValue)
                {
                    throw new InvalidOperationException("Produced-quantity source inventory is invalid.");
                }

                sources.Add(sourceId);
                if (!_outcomes.TryGetValue(sourceId, out var published) ||
                    published.Revision.Value > referenceTimeRevision.Value)
                {
                    continue;
                }

                var outcome = published.Outcome;
                if (outcome.CompanyId != fact.CompanyId || outcome.SiteId != fact.SiteId ||
                    outcome.MachineId != fact.MachineId || outcome.ShiftOccurrenceId != input.ShiftOccurrenceId ||
                    outcome.ProductionDayId != input.ProductionDayId ||
                    outcome.PartId != fact.PartId || outcome.OperationId != fact.OperationId ||
                    outcome.OccurredAtUtc != fact.StartsAtUtc || outcome.ProducedUnits != (int)fact.Value)
                {
                    throw new InvalidOperationException("Reference-time outcome does not match its aggregated source.");
                }

                outcomes.Add(outcome);
            }
        }

        return ProductionReferenceTimeCompleteness.IsComplete(
            sources,
            outcomes,
            aggregationRevision.StreamId.MachineId,
            periodId is OperationalMetricPeriodId.Shift shift ? shift.ShiftOccurrenceId : null,
            periodId is OperationalMetricPeriodId.ProductionDay day ? day.ProductionDayId : null);
    }

    private sealed record PublishedOutcome(
        ProductionReferenceTimeResolution Outcome,
        ProductionReferenceTimeAuthorityRevision Revision);
}
