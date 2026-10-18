using FactoryConnect.Abstractions;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>Required historical authority is unavailable; never an ordinary metric reason.</summary>
internal sealed class MissingContainmentHistoricalAuthorityException(string message) : Exception(message), IOperationalMetricReportingReadFailure
{ public OperationalMetricReportingReadFailure ReportingFailure => OperationalMetricReportingReadFailure.MissingHistoricalAuthority; }

/// <summary>Historical evidence exists but contradicts its claimed identity or content.</summary>
internal sealed class ContainmentHistoricalIntegrityException(string message, Exception? innerException = null)
    : Exception(message, innerException), IOperationalMetricReportingReadFailure
{ public OperationalMetricReportingReadFailure ReportingFailure => OperationalMetricReportingReadFailure.Integrity; }
