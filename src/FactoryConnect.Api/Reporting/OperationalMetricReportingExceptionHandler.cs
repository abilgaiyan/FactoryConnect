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
