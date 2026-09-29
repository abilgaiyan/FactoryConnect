using FactoryConnect.Abstractions;
using FactoryConnect.Core;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>
/// Canonical publication boundary for durable production reference-time outcomes.
/// Validates the D11-D15 outcome against source evidence and the recorded production-standard
/// authority cut before claim-bound transactional outcome admission.
/// </summary>
internal sealed class SqlServerCanonicalProductionReferenceTimePublisher
{
    private readonly SqlServerProductionReferenceTimeTransitionOutcomeStore _store;

    public SqlServerCanonicalProductionReferenceTimePublisher(string connectionString)
        : this(new SqlServerProductionReferenceTimeTransitionOutcomeStore(connectionString))
    {
    }

    internal SqlServerCanonicalProductionReferenceTimePublisher(
        SqlServerProductionReferenceTimeTransitionOutcomeStore store)
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

        ProductionStandardResolver.ValidateCanonicalOutcome(
            evidence,
            shiftOccurrenceId,
            productionDayId,
            standardAuthorityCut,
            proposed.Resolution);

        return await _store.AdmitAsync(
            processorId,
            aggregationPosition,
            proposed,
            cancellationToken).ConfigureAwait(false);
    }
}
