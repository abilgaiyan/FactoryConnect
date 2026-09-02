using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.Api.Reporting;

internal static class OperationalMetricReportingProblemDetails
{
    private const string InvalidRequestType =
        "urn:factoryconnect:problem:reporting:invalid-request";
    private const string MalformedContinuationTokenType =
        "urn:factoryconnect:problem:reporting:malformed-continuation-token";
    private const string IncompatibleContinuationTokenType =
        "urn:factoryconnect:problem:reporting:incompatible-continuation-token";
    private const string ProductionDayShiftRosterCoverageRequiredType =
        "urn:factoryconnect:problem:reporting:production-day-shift-roster-coverage-required";

    public static IResult InvalidRequest() =>
        Problem(
            InvalidRequestType,
            "Invalid reporting query",
            "The reporting query request contains invalid or contradictory filters.",
            StatusCodes.Status400BadRequest,
            "invalid-reporting-query");

    public static async Task<IResult> ExecuteAsync(
        Func<CancellationToken, ValueTask<ReportingPage<OperationalMetricQueryItem>>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        try
        {
            var page = await operation(cancellationToken).ConfigureAwait(false);
            return Results.Ok(OperationalMetricHttpMapper.ToResponse(page));
        }
        catch (ArgumentException exception)
        {
            var problem = TryContinuationTokenProblem(exception);
            if (problem is null)
            {
                throw;
            }

            return problem;
        }
    }

    public static async Task<IResult> ExecuteProductionDayShiftsAsync(
        Func<CancellationToken, ValueTask<ReportingPage<ProductionDayShiftOperationalMetricReport>>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        try
        {
            var page = await operation(cancellationToken).ConfigureAwait(false);
            return Results.Ok(OperationalMetricHttpMapper.ToResponse(page));
        }
        catch (ProductionDayShiftRosterCoverageRequiredException exception)
        {
            return Results.Problem(
                type: ProductionDayShiftRosterCoverageRequiredType,
                title: "Production-day shift roster coverage required",
                statusCode: StatusCodes.Status409Conflict,
                detail: "Authoritative machine-shift roster coverage has not been materialized for the requested machine and production day.",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "production-day-shift-roster-coverage-required",
                    ["machineId"] = exception.MachineId.Value,
                    ["siteId"] = exception.ProductionDayId.SiteId.Value,
                    ["businessDate"] = exception.ProductionDayId.BusinessDate,
                });
        }
        catch (ArgumentException exception)
        {
            var problem = TryContinuationTokenProblem(exception);
            if (problem is null)
            {
                throw;
            }

            return problem;
        }
    }

    private static IResult? TryContinuationTokenProblem(ArgumentException exception)
    {
        if (!OperationalMetricReportingQueryFailureClassifier.TryClassify(
                exception,
                out var failure))
        {
            return null;
        }

        return failure switch
        {
            OperationalMetricReportingQueryFailure.MalformedContinuationToken => Problem(
                MalformedContinuationTokenType,
                "Malformed continuation token",
                "The continuation token is malformed or uses an unsupported format.",
                StatusCodes.Status400BadRequest,
                "malformed-continuation-token"),
            OperationalMetricReportingQueryFailure.IncompatibleContinuationToken => Problem(
                IncompatibleContinuationTokenType,
                "Incompatible continuation token",
                "The continuation token does not belong to this reporting query.",
                StatusCodes.Status400BadRequest,
                "incompatible-continuation-token"),
            _ => throw new InvalidOperationException(
                "The reporting query failure classifier returned an unsupported failure value."),
        };
    }

    private static IResult Problem(
        string type,
        string title,
        string detail,
        int statusCode,
        string code) =>
        Results.Problem(
            type: type,
            title: title,
            statusCode: statusCode,
            detail: detail,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
            });
}
