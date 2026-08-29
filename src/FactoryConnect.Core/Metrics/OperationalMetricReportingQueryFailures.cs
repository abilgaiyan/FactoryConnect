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

        switch (exception)
        {
            case MalformedReportingContinuationTokenException:
                failure = OperationalMetricReportingQueryFailure.MalformedContinuationToken;
                return true;
            case IncompatibleReportingContinuationTokenException:
                failure = OperationalMetricReportingQueryFailure.IncompatibleContinuationToken;
                return true;
            default:
                failure = default;
                return false;
        }
    }
}

internal sealed class MalformedReportingContinuationTokenException(
    string message,
    Exception innerException)
    : ArgumentException(message, "token", innerException);

internal sealed class IncompatibleReportingContinuationTokenException(string message)
    : ArgumentException(message, "token");
