using FactoryConnect.Abstractions;

namespace FactoryConnect.Core;

/// <summary>Owns derived outcomes and reconciles them with the FC-026 inventory at an exact revision.</summary>
public sealed class InMemoryProductionReferenceTimeAuthority
{
    private readonly object _sync = new();
    private readonly Dictionary<ProductionQuantityEvidenceId, ProductionReferenceTimeResolution> _outcomes = [];

    public ProductionReferenceTimeResolution ResolveAndRecord(
        ProductionQuantityEvidence evidence,
        ShiftOccurrenceId shift,
        ProductionDayId day,
        ProductionStandardAuthorityCut cut)
    {
        var outcome = ProductionStandardResolver.Resolve(evidence, shift, day, cut);
        lock (_sync)
        {
            if (_outcomes.TryGetValue(outcome.SourceQuantityEvidenceId, out var existing))
            {
                if (existing with { ConflictingStandardVersionIds = outcome.ConflictingStandardVersionIds } != outcome ||
                    !existing.ConflictingStandardVersionIds.SequenceEqual(
                        outcome.ConflictingStandardVersionIds, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Ordinary replay cannot change a reference-time source outcome or its authority cut.");
                }

                return existing;
            }

            _outcomes.Add(outcome.SourceQuantityEvidenceId, outcome);
            return outcome;
        }
    }

    public bool IsCompleteAtRevision(
        InMemoryMetricAggregationStore aggregationStore,
        MetricAggregationCheckpoint revision,
        OperationalMetricPeriodId periodId)
    {
        ArgumentNullException.ThrowIfNull(aggregationStore);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(periodId);

        // The aggregation store, rather than a caller, owns the inventory at the exact revision.
        var produced = aggregationStore.ReadProducedQuantityAtRevision(revision, periodId);
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
                if (!_outcomes.TryGetValue(sourceId, out var outcome))
                {
                    continue;
                }

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
            revision.StreamId.MachineId,
            periodId is OperationalMetricPeriodId.Shift shift ? shift.ShiftOccurrenceId : null,
            periodId is OperationalMetricPeriodId.ProductionDay day ? day.ProductionDayId : null);
    }
}
