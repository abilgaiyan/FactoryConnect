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

        if (!StringComparer.Ordinal.Equals(exception.ParamName, "token"))
        {
            failure = default;
            return false;
        }

        failure = exception.InnerException is null
            ? OperationalMetricReportingQueryFailure.IncompatibleContinuationToken
            : OperationalMetricReportingQueryFailure.MalformedContinuationToken;
        return true;
    }
}
