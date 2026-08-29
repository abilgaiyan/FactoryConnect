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

        return endpoints;
    }

    internal static async Task<IResult> QueryShiftsAsync(
        ShiftOperationalMetricQueryRequest request,
        IOperationalMetricQueryReader reader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reader);

        var page = await reader.ReadAsync(
            OperationalMetricHttpMapper.ToQuery(request),
            cancellationToken);
        return Results.Ok(OperationalMetricHttpMapper.ToResponse(page));
    }

    internal static async Task<IResult> QueryProductionDaysAsync(
        ProductionDayOperationalMetricQueryRequest request,
        IOperationalMetricQueryReader reader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reader);

        var page = await reader.ReadAsync(
            OperationalMetricHttpMapper.ToQuery(request),
            cancellationToken);
        return Results.Ok(OperationalMetricHttpMapper.ToResponse(page));
    }
}
