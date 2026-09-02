namespace FactoryConnect.Abstractions;

public sealed record ProductionDayShiftOperationalMetricPageQuery
{
    public ProductionDayShiftOperationalMetricPageQuery(
        ProductionDayShiftOperationalMetricQuery selection,
        ReportingPageRequest page)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(page);
        Selection = selection;
        Page = page;
    }

    public ProductionDayShiftOperationalMetricQuery Selection { get; }

    public ReportingPageRequest Page { get; }
}

public interface IProductionDayShiftOperationalMetricQueryReader
{
    ValueTask<ReportingPage<ProductionDayShiftOperationalMetricReport>> ReadAsync(
        ProductionDayShiftOperationalMetricPageQuery query,
        CancellationToken cancellationToken);
}
