using System.Collections.ObjectModel;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed class SqlServerOperationalMetricReportingQueryProvider :
    IOperationalMetricReportingQueryProvider
{
    private readonly SqlServerOperationalMetricProjectionSummaryReader _summaryReader;

    public SqlServerOperationalMetricReportingQueryProvider(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _summaryReader = new SqlServerOperationalMetricProjectionSummaryReader(connectionString);
    }

    public async ValueTask<IReadOnlyList<OperationalMetricProjectionSummary>> ReadWindowAsync(
        OperationalMetricReportQuery query,
        OperationalMetricEvaluationKey? startAfter,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
        cancellationToken.ThrowIfCancellationRequested();

        var comparer = OperationalMetricReportOrdering.GetEvaluationKeyComparer(query.Order);
        var combined = new List<OperationalMetricProjectionSummary>();

        foreach (var source in query.Sources.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentPublication = await _summaryReader.ReadCurrentPublicationSummariesAsync(
                source.ProcessorId,
                cancellationToken);
            combined.AddRange(currentPublication);
        }

        var window = combined
            .Where(summary => OperationalMetricReportQuerySemantics.Matches(query, summary))
            .Where(summary => startAfter is null || comparer.Compare(summary.Key, startAfter) > 0)
            .OrderBy(static summary => summary.Key, comparer)
            .Take(maximumCount)
            .ToArray();

        return new ReadOnlyCollection<OperationalMetricProjectionSummary>(window);
    }
}
