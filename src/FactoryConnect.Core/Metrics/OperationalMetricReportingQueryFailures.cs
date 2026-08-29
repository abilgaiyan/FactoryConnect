namespace FactoryConnect.Core.Metrics;

public enum OperationalMetricReportingQueryFailure
{
    MalformedContinuationToken,
    IncompatibleContinuationToken,
}

public static class OperationalMetricReportingQueryFailureClassifier
{
    public static bool TryClassify(
        ArgumentException exception,
        out OperationalMetricReportingQueryFailure failure)
    {
        ArgumentNullException.ThrowIfNull(exception);

        failure = exception switch
        {
            MalformedReportingContinuationTokenException =>
                OperationalMetricReportingQueryFailure.MalformedContinuationToken,
            IncompatibleReportingContinuationTokenException =>
                OperationalMetricReportingQueryFailure.IncompatibleContinuationToken,
            _ => default,
        };

        return exception is
            MalformedReportingContinuationTokenException or
            IncompatibleReportingContinuationTokenException;
    }
}

internal sealed class MalformedReportingContinuationTokenException(
    Exception innerException)
    : ArgumentException(
        "Continuation token is malformed.",
        "token",
        innerException);

internal sealed class IncompatibleReportingContinuationTokenException()
    : ArgumentException(
        "Continuation token does not belong to the requested reporting query.",
        "token");
