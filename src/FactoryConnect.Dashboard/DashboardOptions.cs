namespace FactoryConnect.Dashboard;

public sealed class DashboardOptions
{
    public const string SectionName = "Dashboard";

    public string ReportingApiBaseAddress { get; init; } = string.Empty;

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public List<DashboardSourceOptions> Sources { get; init; } = [];
}

public sealed class DashboardSourceOptions
{
    public Guid MachineId { get; init; }

    public string ProcessorId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;
}
