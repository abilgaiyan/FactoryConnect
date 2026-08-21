using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Configuration;

public static class SiteConfigurationResolver
{
    public static SiteConfigurationVersion? Resolve(
        IEnumerable<SiteConfigurationVersion> configurations,
        CompanyId companyId,
        SiteId siteId,
        DateTimeOffset reportTimestamp)
    {
        ArgumentNullException.ThrowIfNull(configurations);

        return configurations
            .Where(configuration =>
                configuration.CompanyId == companyId &&
                configuration.SiteId == siteId &&
                configuration.IsEffectiveAt(reportTimestamp))
            .OrderByDescending(configuration => configuration.EffectiveFrom)
            .FirstOrDefault();
    }

    public static MetricPolicyDefinition? ResolveMetricPolicy(
        SiteConfigurationVersion configuration,
        string metricKey)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(metricKey);

        return configuration.MetricPolicies.FirstOrDefault(policy =>
            string.Equals(
                policy.MetricKey,
                metricKey,
                StringComparison.OrdinalIgnoreCase));
    }
}
