namespace FactoryConnect.Abstractions;

public sealed record SiteConfigurationVersion
{
    public required CompanyId CompanyId { get; init; }
    public required SiteId SiteId { get; init; }
    public required ConfigurationVersion Version { get; init; }
    public ConfigurationLifecycle Lifecycle { get; init; } = ConfigurationLifecycle.Draft;
    public required DateTimeOffset EffectiveFrom { get; init; }
    public DateTimeOffset? EffectiveTo { get; init; }
    public IReadOnlyList<MetricPolicyDefinition> MetricPolicies { get; init; } = [];

    public bool IsEffectiveAt(DateTimeOffset timestamp) =>
        Lifecycle == ConfigurationLifecycle.Published &&
        EffectiveFrom <= timestamp &&
        (EffectiveTo is null || timestamp < EffectiveTo.Value);
}
