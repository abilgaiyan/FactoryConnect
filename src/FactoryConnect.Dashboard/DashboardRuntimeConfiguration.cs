namespace FactoryConnect.Dashboard;

public sealed record DashboardRuntimeConfiguration(
    string ReportingBasePath,
    int RequestTimeoutMilliseconds,
    IReadOnlyList<DashboardRuntimeSource> Sources);

public sealed record DashboardRuntimeSource(
    Guid MachineId,
    string ProcessorId,
    string SiteId,
    string ProductionLineId,
    string DisplayName,
    string? GroupName,
    int DisplayOrder);
