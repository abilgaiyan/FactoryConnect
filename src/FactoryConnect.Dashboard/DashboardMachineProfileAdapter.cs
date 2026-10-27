namespace FactoryConnect.Dashboard;

// Read-only, explicitly commissioned metadata. This does not activate acquisition or metrics.
public sealed class DashboardMachineProfileOptions
{
    public Guid MachineId { get; init; }
    public string ProfileId { get; init; } = string.Empty;
    public string ProfileVersion { get; init; } = string.Empty;
    public string IntegrationCategoryId { get; init; } = string.Empty;
    public string ApprovalReference { get; init; } = string.Empty;
    public DateTimeOffset? EffectiveFrom { get; init; }
    public List<DashboardCapabilityOptions> Capabilities { get; init; } = [];
    public string? FaultLabel { get; init; }
    public string? StoppedLabel { get; init; }
}

public sealed class DashboardCapabilityOptions
{
    public string Key { get; init; } = string.Empty;
    public string Support { get; init; } = "not-assessed";
    public string Qualification { get; init; } = "not-assessed";
}

public sealed record DashboardRuntimeCapability(string Key, string Support, string Qualification);
public sealed record DashboardRuntimeMachineProfile(
    Guid MachineId, string ProfileId, string ProfileVersion, string IntegrationCategoryId,
    string ApprovalReference, DateTimeOffset? EffectiveFrom, string? StoppedLabel, string? FaultLabel,
    IReadOnlyList<DashboardRuntimeCapability> Capabilities);

public static class DashboardMachineProfileAdapter
{
    public static IReadOnlyList<DashboardRuntimeMachineProfile> Project(DashboardOptions options) =>
        options.MachineProfiles.Select(static profile => new DashboardRuntimeMachineProfile(
            profile.MachineId, profile.ProfileId, profile.ProfileVersion, profile.IntegrationCategoryId,
            profile.ApprovalReference, profile.EffectiveFrom, profile.StoppedLabel, profile.FaultLabel,
            profile.Capabilities.Select(static capability => new DashboardRuntimeCapability(
                capability.Key, capability.Support, capability.Qualification)).ToArray())).ToArray();

    public static void Validate(DashboardOptions options, List<string> failures)
    {
        foreach (var group in options.Sources.GroupBy(static source => source.MachineId))
        {
            if (group.Select(static source => (source.SiteId, source.ProductionLineId, source.DisplayName,
                source.GroupName, source.DisplayOrder)).Distinct().Skip(1).Any())
            {
                failures.Add($"Conflicting dashboard source metadata for machine {group.Key}.");
            }
        }

        var identities = new HashSet<Guid>();
        foreach (var profile in options.MachineProfiles)
        {
            if (!identities.Add(profile.MachineId) ||
                !options.Sources.Any(source => source.MachineId == profile.MachineId))
            {
                failures.Add("Machine profiles must bind uniquely to registered dashboard machines.");
            }
            if (string.IsNullOrWhiteSpace(profile.ProfileId) || string.IsNullOrWhiteSpace(profile.ProfileVersion) ||
                string.IsNullOrWhiteSpace(profile.IntegrationCategoryId) || string.IsNullOrWhiteSpace(profile.ApprovalReference) ||
                profile.EffectiveFrom is null)
            {
                failures.Add("Machine profiles require identity, version, category, approval reference and effective date.");
            }
            if (profile.StoppedLabel is not null and not "Machine OFF")
            {
                failures.Add("Only the approved Machine OFF stopped label is supported.");
            }
            if (profile.FaultLabel is not null and not "Alarm")
            {
                failures.Add("Only the approved Alarm fault label is supported.");
            }
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var capability in profile.Capabilities)
            {
                if (string.IsNullOrWhiteSpace(capability.Key) || !keys.Add(capability.Key) ||
                    capability.Support is not ("supported" or "unsupported" or "not-assessed") ||
                    capability.Qualification is not ("qualified" or "pending" or "rejected" or "not-assessed") ||
                    (capability.Qualification == "qualified" && capability.Support != "supported"))
                {
                    failures.Add("Invalid or duplicate machine capability metadata.");
                }
            }
            if ((profile.StoppedLabel is not null || profile.FaultLabel is not null) && !profile.Capabilities.Any(static capability =>
                capability.Key == "operational-state" && capability.Support == "supported" && capability.Qualification == "qualified"))
            {
                failures.Add("An approved state label requires qualified operational-state support.");
            }
        }
    }
}
