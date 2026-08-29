namespace FactoryConnect.Api.Reporting;

internal static class OperationalMetricHttpRequestValidator
{
    public static void Validate(ShiftOperationalMetricQueryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateCommon(
            request.Sources,
            request.Metrics,
            request.Statuses,
            request.Order);
    }

    public static void Validate(ProductionDayOperationalMetricQueryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateCommon(
            request.Sources,
            request.Metrics,
            request.Statuses,
            request.Order);
    }

    private static void ValidateCommon(
        IReadOnlyList<ReportingSourceRequest>? sources,
        IReadOnlyList<OperationalMetricDefinitionRequest>? metrics,
        IReadOnlyList<string>? statuses,
        string? order)
    {
        if (sources is null)
        {
            throw new ArgumentException("Reporting sources are required.", nameof(sources));
        }

        if (sources.Any(static source => source is null))
        {
            throw new ArgumentException(
                "Reporting sources cannot contain null values.",
                nameof(sources));
        }

        if (metrics?.Any(static metric => metric is null) == true)
        {
            throw new ArgumentException(
                "Metric filters cannot contain null values.",
                nameof(metrics));
        }

        if (statuses?.Any(static status => status is null) == true)
        {
            throw new ArgumentException(
                "Status filters cannot contain null values.",
                nameof(statuses));
        }

        if (string.IsNullOrWhiteSpace(order))
        {
            throw new ArgumentException("Reporting order is required.", nameof(order));
        }
    }
}
