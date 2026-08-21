using FactoryConnect.Abstractions;
using FactoryConnect.Core.Configuration;

namespace FactoryConnect.Core.Tests;

public sealed class SiteConfigurationResolverTests
{
    [Fact]
    public void DraftConfigurationIsNotEffective()
    {
        var configuration = CreateConfiguration(
            "1.0",
            ConfigurationLifecycle.Draft,
            At(2026, 8, 1));

        Assert.False(configuration.IsEffectiveAt(At(2026, 8, 21)));
    }

    [Fact]
    public void PublishedConfigurationIsEffectiveInsideItsWindow()
    {
        var configuration = CreateConfiguration(
            "1.0",
            ConfigurationLifecycle.Published,
            At(2026, 8, 1),
            At(2026, 9, 1));

        Assert.True(configuration.IsEffectiveAt(At(2026, 8, 21)));
        Assert.False(configuration.IsEffectiveAt(At(2026, 9, 1)));
    }

    [Fact]
    public void ResolveSelectsConfigurationEffectiveForReportDate()
    {
        var first = CreateConfiguration(
            "1.0",
            ConfigurationLifecycle.Published,
            At(2026, 8, 1),
            At(2026, 9, 1));
        var second = CreateConfiguration(
            "2.0",
            ConfigurationLifecycle.Published,
            At(2026, 9, 1));

        var resolved = SiteConfigurationResolver.Resolve(
            [first, second],
            first.CompanyId,
            first.SiteId,
            At(2026, 8, 21));

        Assert.NotNull(resolved);
        Assert.Equal("1.0", resolved.Version.Value);
    }

    [Fact]
    public void ResolveKeepsCompanyAndSiteConfigurationsIsolated()
    {
        var expected = CreateConfiguration(
            "1.0",
            ConfigurationLifecycle.Published,
            At(2026, 8, 1));
        var otherSite = expected with
        {
            SiteId = new SiteId("SITE-2"),
            Version = new ConfigurationVersion("9.0"),
        };

        var resolved = SiteConfigurationResolver.Resolve(
            [otherSite, expected],
            expected.CompanyId,
            expected.SiteId,
            At(2026, 8, 21));

        Assert.NotNull(resolved);
        Assert.Equal(expected.SiteId, resolved.SiteId);
        Assert.Equal("1.0", resolved.Version.Value);
    }

    [Fact]
    public void ResolveMetricPolicySelectsConfiguredStrategy()
    {
        var configuration = CreateConfiguration(
            "1.0",
            ConfigurationLifecycle.Published,
            At(2026, 8, 1)) with
        {
            MetricPolicies =
            [
                new MetricPolicyDefinition
                {
                    MetricKey = CanonicalMetricKeys.Availability,
                    StrategyKey = "apt-over-pot",
                },
                new MetricPolicyDefinition
                {
                    MetricKey = CanonicalMetricKeys.Performance,
                    StrategyKey = "reference-time-over-apt",
                },
            ],
        };

        var policy = SiteConfigurationResolver.ResolveMetricPolicy(
            configuration,
            CanonicalMetricKeys.Availability);

        Assert.NotNull(policy);
        Assert.Equal("apt-over-pot", policy.StrategyKey);
    }

    private static SiteConfigurationVersion CreateConfiguration(
        string version,
        ConfigurationLifecycle lifecycle,
        DateTimeOffset effectiveFrom,
        DateTimeOffset? effectiveTo = null) =>
        new()
        {
            CompanyId = new CompanyId("COMPANY-1"),
            SiteId = new SiteId("SITE-1"),
            Version = new ConfigurationVersion(version),
            Lifecycle = lifecycle,
            EffectiveFrom = effectiveFrom,
            EffectiveTo = effectiveTo,
        };

    private static DateTimeOffset At(int year, int month, int day) =>
        new(year, month, day, 0, 0, 0, TimeSpan.Zero);
}
