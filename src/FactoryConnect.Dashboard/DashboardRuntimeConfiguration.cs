namespace FactoryConnect.Dashboard;

public sealed record DashboardRuntimeConfiguration(
    string ReportingBasePath,
    int RequestTimeoutMilliseconds,
    IReadOnlyList<DashboardRuntimeSource> Sources,
    int MachineProfileSchemaVersion = 1,
    IReadOnlyList<DashboardRuntimeMachineProfile>? MachineProfiles = null);

public sealed record DashboardRuntimeSource(
    Guid MachineId,
    string ProcessorId,
    string SiteId,
    string ProductionLineId,
    string DisplayName,
    string? GroupName,
    int DisplayOrder);
