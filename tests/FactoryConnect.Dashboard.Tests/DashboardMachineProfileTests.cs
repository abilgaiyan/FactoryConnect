using FactoryConnect.Dashboard;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace FactoryConnect.Dashboard.Tests;

public sealed class DashboardMachineProfileTests
{
    private static readonly Guid MachineId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void LegacyConfigurationHasNoAssumedProfile()
    {
        Assert.Empty(DashboardMachineProfileAdapter.Project(Options()));
        Assert.True(new DashboardOptionsValidator(new Environment()).Validate(null, Options()).Succeeded);
    }

    [Fact]
    public void ProfileProjectsExplicitMetadataWithoutUpstreamAddress()
    {
        var options = Options([Profile()]);
        Assert.True(new DashboardOptionsValidator(new Environment()).Validate(null, options).Succeeded);
        var profile = Assert.Single(DashboardMachineProfileAdapter.Project(options));
        Assert.Equal("Machine OFF", profile.StoppedLabel);
        Assert.Equal("qualified", Assert.Single(profile.Capabilities).Qualification);
        var json = System.Text.Json.JsonSerializer.Serialize(profile);
        Assert.DoesNotContain(options.ReportingApiBaseAddress, json, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateAndUnregisteredBindingsFail()
    {
        Assert.True(new DashboardOptionsValidator(new Environment()).Validate(null, Options([Profile(), Profile()])).Failed);
        var unregistered = Options([Profile()]);
        unregistered.Sources.Clear();
        Assert.True(new DashboardOptionsValidator(new Environment()).Validate(null, unregistered).Failed);
    }

    [Fact]
    public void ConflictingSourceMetadataFailsEvenAcrossProcessors()
    {
        var options = Options();
        options.Sources.Add(new DashboardSourceOptions
        {
            MachineId = MachineId, ProcessorId = "other", SiteId = "different-site",
            ProductionLineId = "line", DisplayName = "Machine"
        });
        Assert.True(new DashboardOptionsValidator(new Environment()).Validate(null, options).Failed);
    }

    [Theory]
    [InlineData("unsupported", "qualified")]
    [InlineData("supported", "invented")]
    [InlineData("invalid", "pending")]
    public void InvalidCapabilitiesFail(string support, string qualification)
    {
        var profile = new DashboardMachineProfileOptions
        {
            MachineId = MachineId, ProfileId = "profile", ProfileVersion = "1", IntegrationCategoryId = "digital",
            ApprovalReference = "test", EffectiveFrom = DateTimeOffset.UnixEpoch,
            Capabilities = [new() { Key = "operational-state", Support = support, Qualification = qualification }]
        };
        Assert.True(new DashboardOptionsValidator(new Environment()).Validate(null, Options([profile])).Failed);
    }

    [Fact]
    public void UnknownCapabilityKeysArePreservedWithoutEnablingAnything()
    {
        var profile = Profile();
        profile.Capabilities.Add(new() { Key = "future-signal", Support = "not-assessed", Qualification = "pending" });
        var options = Options([profile]);
        Assert.True(new DashboardOptionsValidator(new Environment()).Validate(null, options).Succeeded);
        Assert.Equal(2, Assert.Single(DashboardMachineProfileAdapter.Project(options)).Capabilities.Count);
    }

    private static DashboardMachineProfileOptions Profile() => new()
    {
        MachineId = MachineId, ProfileId = "digital-state", ProfileVersion = "1", IntegrationCategoryId = "digital",
        ApprovalReference = "factory-confirmed-signal-meaning", EffectiveFrom = DateTimeOffset.UnixEpoch,
        StoppedLabel = "Machine OFF",
        Capabilities = [new() { Key = "operational-state", Support = "supported", Qualification = "qualified" }]
    };

    private static DashboardOptions Options(List<DashboardMachineProfileOptions>? profiles = null) => new()
    {
        ReportingApiBaseAddress = "http://factory-server:5080", MachineProfiles = profiles ?? [],
        Sources = [new() { MachineId = MachineId, ProcessorId = "processor", SiteId = "site", ProductionLineId = "line", DisplayName = "Machine" }]
    };

    private sealed class Environment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
