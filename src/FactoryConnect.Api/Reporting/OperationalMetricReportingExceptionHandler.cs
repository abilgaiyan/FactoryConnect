using FactoryConnect.Abstractions;
using Microsoft.AspNetCore.Diagnostics;

namespace FactoryConnect.Api.Reporting;

internal sealed class OperationalMetricReportingExceptionHandler : IExceptionHandler
{
    private static readonly PathString ReportingPath =
        new("/api/reporting/v1/operational-metrics");

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        if (httpContext.Request.Path.StartsWithSegments(ReportingPath) && exception is IOperationalMetricReportingReadFailure failure)
        {
            var (status, code, title) = failure.ReportingFailure switch
            {
                OperationalMetricReportingReadFailure.UnsupportedCodec => (501, "unsupported-reporting-codec", "Reporting evidence codec is unsupported"),
                OperationalMetricReportingReadFailure.MissingHistoricalAuthority => (503, "reporting-authority-unavailable", "Historical reporting authority is unavailable"),
                _ => (500, "reporting-evidence-integrity", "Reporting evidence failed verification")
            };
            await Results.Problem(statusCode: status, title: title,
                type: "urn:factoryconnect:problem:reporting:" + code,
                extensions: new Dictionary<string, object?> { ["code"] = code }).ExecuteAsync(httpContext);
            return true;
        }
        if (exception is not BadHttpRequestException ||
            !httpContext.Request.Path.StartsWithSegments(ReportingPath))
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await OperationalMetricReportingProblemDetails.InvalidRequest()
            .ExecuteAsync(httpContext)
            .ConfigureAwait(false);
        return true;
    }
}
