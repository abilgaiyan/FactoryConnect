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

    public static IResult InvalidRequest() =>
        Problem(
            InvalidRequestType,
            "Invalid reporting query",
            "The reporting query request contains invalid or contradictory filters.",
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
            if (!OperationalMetricReportingQueryFailureClassifier.TryClassify(
                    exception,
                    out var failure))
            {
                throw;
            }

            return failure switch
            {
                OperationalMetricReportingQueryFailure.MalformedContinuationToken => Problem(
                    MalformedContinuationTokenType,
                    "Malformed continuation token",
                    "The continuation token is malformed or uses an unsupported format.",
                    "malformed-continuation-token"),
                OperationalMetricReportingQueryFailure.IncompatibleContinuationToken => Problem(
                    IncompatibleContinuationTokenType,
                    "Incompatible continuation token",
                    "The continuation token does not belong to this reporting query.",
                    "incompatible-continuation-token"),
                _ => throw new InvalidOperationException(
                    "The reporting query failure classifier returned an unsupported failure value."),
            };
        }
    }

    private static IResult Problem(
        string type,
        string title,
        string detail,
        string code) =>
        Results.Problem(
            type: type,
            title: title,
            statusCode: StatusCodes.Status400BadRequest,
            detail: detail,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
            });
}
