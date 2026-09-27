using FactoryConnect.Abstractions;
using FactoryConnect.Core;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>
/// Canonical publication boundary for durable production reference-time outcomes.
/// Validates the D11-D15 outcome against source evidence and the recorded production-standard
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

        ProductionStandardResolver.ValidateCanonicalOutcome(
            evidence,
            shiftOccurrenceId,
            productionDayId,
            standardAuthorityCut,
            proposed.Resolution);

        return await _store.PublishAsync(
            processorId,
            aggregationPosition,
            proposed,
            cancellationToken).ConfigureAwait(false);
    }
}
