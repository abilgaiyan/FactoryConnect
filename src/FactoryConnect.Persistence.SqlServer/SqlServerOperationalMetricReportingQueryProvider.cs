using System.Collections.ObjectModel;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed class SqlServerOperationalMetricReportingQueryProvider :
    IOperationalMetricReportingQueryProvider
{
    private readonly SqlServerOperationalMetricProjectionSummaryReader _summaryReader;
    private readonly SqlServerOperationalMetricProjectionSourceBindingReader _sourceBindingReader;

    public SqlServerOperationalMetricReportingQueryProvider(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _summaryReader = new SqlServerOperationalMetricProjectionSummaryReader(connectionString);
        _sourceBindingReader = new SqlServerOperationalMetricProjectionSourceBindingReader(connectionString);
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

            var persistedMachineId = await _sourceBindingReader.ReadMachineIdAsync(
                source.ProcessorId,
                cancellationToken);
            if (persistedMachineId is not null && persistedMachineId.Value != source.MachineId)
            {
                throw new InvalidOperationException(
                    $"Operational metric reporting source binding for processor '{source.ProcessorId}' does not match requested machine '{source.MachineId}'.");
            }

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
