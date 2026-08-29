using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Metrics;

public sealed class OperationalMetricQueryReader : IOperationalMetricQueryReader
{
    private readonly IOperationalMetricReportingQueryReader _reader;

    public OperationalMetricQueryReader(IOperationalMetricReportingQueryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
    }

    public async ValueTask<ReportingPage<OperationalMetricQueryItem>> ReadAsync(
        OperationalMetricReportQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var page = await _reader.ReadAsync(query, cancellationToken).ConfigureAwait(false);
        var items = page.Items
            .Select(OperationalMetricQueryItem.FromSummary)
            .ToArray();

        return new ReportingPage<OperationalMetricQueryItem>(
            items,
            page.ContinuationToken);
    }
}
