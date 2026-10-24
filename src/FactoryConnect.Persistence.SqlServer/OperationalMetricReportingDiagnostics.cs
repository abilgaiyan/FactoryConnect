using System.Diagnostics;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>Opt-in spans; no SQL text, connection strings or evidence payloads are recorded.</summary>
internal static class OperationalMetricReportingDiagnostics
{
    internal static readonly ActivitySource Source = new("FactoryConnect.Reporting");
}
