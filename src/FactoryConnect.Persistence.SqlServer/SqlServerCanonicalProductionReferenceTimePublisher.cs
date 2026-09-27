using FactoryConnect.Abstractions;
using FactoryConnect.Core;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>
/// Canonical publication boundary for durable production reference-time outcomes.
/// Recomputes the D11-D15 outcome from source evidence and the recorded production-standard
/// authority cut before delegating transactional persistence to the SQL outcome store.
/// </summary>
public sealed class SqlServerCanonicalProductionReferenceTimePublisher
{
    private readonly SqlServerProductionReferenceTimeOutcomeStore _store;

    public SqlServerCanonicalProductionReferenceTimePublisher(string connectionString)
        : this(new SqlServerProductionReferenceTimeOutcomeStore(connectionString))
    {
    }

    internal SqlServerCanonicalProductionReferenceTimePublisher(
        SqlServerProductionReferenceTimeOutcomeStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public async Task<PublishedProductionReferenceTimeOutcome> PublishAsync(
        MetricAggregationProcessorId processorId,
        MetricInputPosition aggregationPosition,
        ProductionQuantityEvidence evidence,
        ShiftOccurrenceId shiftOccurrenceId,
        ProductionDayId productionDayId,
        ProductionStandardAuthorityCut standardAuthorityCut,
        PublishedProductionReferenceTimeOutcome proposed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(shiftOccurrenceId);
        ArgumentNullException.ThrowIfNull(productionDayId);
        ArgumentNullException.ThrowIfNull(standardAuthorityCut);
        ArgumentNullException.ThrowIfNull(proposed);

        var canonical = ProductionStandardResolver.Resolve(
            evidence,
            shiftOccurrenceId,
            productionDayId,
            standardAuthorityCut);

        EnsureCanonical(proposed.Resolution, canonical);

        return await _store.PublishAsync(
            processorId,
            aggregationPosition,
            proposed,
            cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureCanonical(
        ProductionReferenceTimeResolution proposed,
        ProductionReferenceTimeResolution canonical)
    {
        proposed.Validate();
        canonical.Validate();

        if (proposed.SourceQuantityEvidenceId != canonical.SourceQuantityEvidenceId ||
            proposed.CompanyId != canonical.CompanyId ||
            proposed.SiteId != canonical.SiteId ||
            proposed.MachineId != canonical.MachineId ||
            proposed.PartId != canonical.PartId ||
            proposed.OperationId != canonical.OperationId ||
            proposed.ShiftOccurrenceId != canonical.ShiftOccurrenceId ||
            proposed.ProductionDayId != canonical.ProductionDayId ||
            proposed.OccurredAtUtc != canonical.OccurredAtUtc ||
            proposed.ProducedUnits != canonical.ProducedUnits ||
            proposed.AuthorityRevision != canonical.AuthorityRevision ||
            proposed.Status != canonical.Status ||
            !string.Equals(proposed.SelectedStandardVersionId, canonical.SelectedStandardVersionId, StringComparison.Ordinal) ||
            !string.Equals(proposed.SelectedStandardSourceReference, canonical.SelectedStandardSourceReference, StringComparison.Ordinal) ||
            proposed.IdealDurationSeconds != canonical.IdealDurationSeconds ||
            !proposed.ConflictingStandardVersionIds.SequenceEqual(
                canonical.ConflictingStandardVersionIds,
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The proposed production reference-time outcome is not canonical for its source evidence and production-standard authority cut.");
        }
    }
}
