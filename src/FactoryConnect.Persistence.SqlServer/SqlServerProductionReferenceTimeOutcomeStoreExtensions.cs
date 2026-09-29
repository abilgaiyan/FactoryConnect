using FactoryConnect.Abstractions;

namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlServerProductionReferenceTimeOutcomeStoreExtensions
{
    public static Task<IReadOnlyList<PublishedProductionReferenceTimeOutcome>> ReadAtRevisionAsync(
        this SqlServerProductionReferenceTimeOutcomeStore store,
        MetricAggregationProcessorId processorId,
        ProductionReferenceTimeAuthorityRevision? revision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (revision is null)
        {
            throw new InvalidOperationException(
                "A completed reference-time transition must identify its durable authority revision.");
        }

        return store.ReadAtRevisionAsync(processorId, revision.Value, cancellationToken);
    }
}
