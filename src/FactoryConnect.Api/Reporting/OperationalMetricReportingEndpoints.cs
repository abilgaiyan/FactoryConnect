using FactoryConnect.Abstractions;

namespace FactoryConnect.Api.Reporting;

public static class OperationalMetricReportingEndpoints
{
    public static IEndpointRouteBuilder MapOperationalMetricReportingEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints
            .MapGroup("/api/reporting/v1/operational-metrics")
            .WithTags("Operational Metrics");

        group.MapPost("/shifts/query", QueryShiftsAsync)
            .WithName("QueryShiftOperationalMetrics")
            .Produces<OperationalMetricPageResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapPost("/production-days/query", QueryProductionDaysAsync)
            .WithName("QueryProductionDayOperationalMetrics")
            .Produces<OperationalMetricPageResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapPost("/production-day-shifts/query", QueryProductionDayShiftsAsync)
            .WithName("QueryProductionDayShiftOperationalMetrics")
            .Produces<ProductionDayShiftOperationalMetricPageResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return endpoints;
    }

    internal static Task<IResult> QueryShiftsAsync(
        ShiftOperationalMetricQueryRequest request,
        IOperationalMetricQueryReader reader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reader);

        ShiftOperationalMetricReportQuery query;
        try
        {
            OperationalMetricHttpRequestValidator.Validate(request);
            query = OperationalMetricHttpMapper.ToQuery(request);
        }
        catch (ArgumentException)
        {
            return Task.FromResult(OperationalMetricReportingProblemDetails.InvalidRequest());
        }

        return OperationalMetricReportingProblemDetails.ExecuteAsync(
            token => reader.ReadAsync(query, token),
            cancellationToken);
    }

    internal static Task<IResult> QueryProductionDaysAsync(
        ProductionDayOperationalMetricQueryRequest request,
        IOperationalMetricQueryReader reader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reader);

        ProductionDayOperationalMetricReportQuery query;
        try
        {
            OperationalMetricHttpRequestValidator.Validate(request);
            query = OperationalMetricHttpMapper.ToQuery(request);
        }
        catch (ArgumentException)
        {
            return Task.FromResult(OperationalMetricReportingProblemDetails.InvalidRequest());
        }

        return OperationalMetricReportingProblemDetails.ExecuteAsync(
            token => reader.ReadAsync(query, token),
            cancellationToken);
    }

    internal static Task<IResult> QueryProductionDayShiftsAsync(
        ProductionDayShiftOperationalMetricQueryRequest request,
        IProductionDayShiftOperationalMetricQueryReader reader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reader);

        ProductionDayShiftOperationalMetricPageQuery query;
        try
        {
            OperationalMetricHttpRequestValidator.Validate(request);
            query = OperationalMetricHttpMapper.ToQuery(request);
        }
        catch (ArgumentException)
        {
            return Task.FromResult(OperationalMetricReportingProblemDetails.InvalidRequest());
        }

        return OperationalMetricReportingProblemDetails.ExecuteProductionDayShiftsAsync(
            token => reader.ReadAsync(query, token),
            cancellationToken);
    }
}
