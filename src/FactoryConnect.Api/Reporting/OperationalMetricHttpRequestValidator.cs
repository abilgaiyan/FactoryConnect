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

    public static void Validate(ProductionDayShiftOperationalMetricQueryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Sources is null)
        {
            throw new ArgumentException("Production-day shift reporting sources are required.", nameof(request));
        }

        if (request.Sources.Any(static source => source is null))
        {
            throw new ArgumentException(
                "Production-day shift reporting sources cannot contain null values.",
                nameof(request));
        }

        ValidateFilters(request.Metrics, request.Statuses);

        if (request.Context is { UnpartitionedOnly: true } context &&
            (context.ProductionOrderId is not null ||
             context.OperationId is not null ||
             context.PartId is not null ||
             context.OperatorId is not null))
        {
            throw new ArgumentException(
                "An unpartitioned production-day shift context cannot also specify context identifiers.",
                nameof(request));
        }
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

        ValidateFilters(metrics, statuses);

        if (string.IsNullOrWhiteSpace(order))
        {
            throw new ArgumentException("Reporting order is required.", nameof(order));
        }
    }

    private static void ValidateFilters(
        IReadOnlyList<OperationalMetricDefinitionRequest>? metrics,
        IReadOnlyList<string>? statuses)
    {
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
    }
}
