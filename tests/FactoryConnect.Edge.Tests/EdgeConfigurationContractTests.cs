using System.Runtime.CompilerServices;
using System.Text.Json;
using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Core.Machines;
using FactoryConnect.Edge;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class EdgeConfigurationContractTests
{
    private const string CommissioningMaximumCurrentAge = "00:00:10";
    private const string CheckedInDeviceKey = "CNC-01";
    private const string CheckedInActivityStreamKey = "mtconnect:CNC-01";
    private const string ProductionTemplateDeviceKey = "__DEVICE_KEY__";
    private const string ProductionTemplateActivityStreamKey = "mtconnect:__DEVICE_KEY__";

    [Fact]
    public void CheckedInRuntimeConfigurationCarriesCommissioningFreshnessAuthority()
    {
        var path = RepositoryPath("src", "FactoryConnect.Edge", "appsettings.Demo.json");

        AssertMaximumCurrentAge(path);
        AssertMtConnectActivityStreamAuthority(
            path,
            CheckedInDeviceKey,
            CheckedInActivityStreamKey);
    }

    [Fact]
    public void CheckedInRuntimeConfigurationReachesSuccessfulRootComposition()
    {
        var path = RepositoryPath("src", "FactoryConnect.Edge", "appsettings.Demo.json");
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(RepositoryPath("src", "FactoryConnect.Edge", "appsettings.json"), optional: false, reloadOnChange: false)
            .AddJsonFile(path, optional: false, reloadOnChange: false)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddFactoryConnectEdgeApplication(configuration);

        using var provider = services.BuildServiceProvider();
        var freshnessPolicy = provider.GetRequiredService<ICurrentStateFreshnessPolicy>();
        Assert.Equal(TimeSpan.FromSeconds(10), freshnessPolicy.MaximumCurrentAge);
    }

    [Fact]
    public void ProductionTemplateCarriesCommissioningFreshnessAuthority()
    {
        var path = RepositoryPath("config", "templates", "edge.production.template.json");

        AssertMaximumCurrentAge(path);
        AssertMtConnectActivityStreamAuthority(
            path,
            ProductionTemplateDeviceKey,
            ProductionTemplateActivityStreamKey);
    }

    [Fact]
    public void EffectiveProductionConfigurationCannotInheritPackagedAssignments()
    {
        var basePath = RepositoryPath("src", "FactoryConnect.Edge", "appsettings.json");
        var configuration = new ConfigurationBuilder().AddJsonFile(basePath)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ProductionProcessing:Machines:0:MachineId"] = "677b030b-1ede-4a67-82ae-6a494791357f",
                ["ProductionProcessing:Machines:0:ActivityStreamKey"] = "mtconnect:GG-003",
            }).Build();
        Assert.Null(configuration["ProductionProcessing:Machines:0:Contexts:0:AssignmentId"]);
        Assert.Null(configuration["ProductionProcessing:Machines:0:PlannedProduction:TimeZoneId"]);
        Assert.Empty(configuration.GetSection("ProductionProcessing:ShiftSchedules").GetChildren());
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => services.AddFactoryConnectProductionMetricInputs(
            configuration, new ObservationStreamId(new MachineId(Guid.Parse("677b030b-1ede-4a67-82ae-6a494791357f")), "mtconnect:GG-003")));
    }

    [Theory]
    [InlineData("Production", false)]
    [InlineData("Demo", true)]
    public void HostEnvironmentSelectsSampleAuthorityExplicitly(string environment, bool hasSample)
    {
        var root = Path.GetDirectoryName(RepositoryPath("src", "FactoryConnect.Edge", "appsettings.json"))!;
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = environment, ContentRootPath = root, Args = [],
        });
        Assert.Equal(hasSample, builder.Configuration["ProductionProcessing:Machines:0:Contexts:0:AssignmentId"] == "CTX-1");
        Assert.Equal(hasSample, builder.Configuration["ProductionProcessing:Machines:0:PlannedProduction:AssignmentId"] == "POT-1");
        if (!hasSample)
        {
            builder.Services.AddLogging();
            Assert.ThrowsAny<Exception>(() => builder.Services.AddFactoryConnectEdgeApplication(builder.Configuration));
        }
    }

    private static void AssertMaximumCurrentAge(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var maximumCurrentAge = document.RootElement
            .GetProperty("CurrentState")
            .GetProperty("Freshness")
            .GetProperty("MaximumCurrentAge")
            .GetString();

        Assert.Equal(CommissioningMaximumCurrentAge, maximumCurrentAge);
    }

    private static void AssertMtConnectActivityStreamAuthority(
        string path,
        string expectedDeviceKey,
        string expectedActivityStreamKey)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var deviceKey = document.RootElement
            .GetProperty("MTConnect")
            .GetProperty("Machines")[0]
            .GetProperty("DeviceKey")
            .GetString();
        var activityStreamKey = document.RootElement
            .GetProperty("ProductionProcessing")
            .GetProperty("Machines")[0]
            .GetProperty("ActivityStreamKey")
            .GetString();

        Assert.Equal(expectedDeviceKey, deviceKey);
        Assert.Equal(expectedActivityStreamKey, activityStreamKey);
    }

    private static string RepositoryPath(
        string first,
        string second,
        string third,
        [CallerFilePath] string sourceFilePath = "")
    {
        var projectDirectory = Directory.GetParent(sourceFilePath)?.FullName
            ?? throw new InvalidOperationException("Unable to locate the test project directory.");
        var testsDirectory = Directory.GetParent(projectDirectory)?.FullName
            ?? throw new InvalidOperationException("Unable to locate the tests directory.");
        var repositoryRoot = Directory.GetParent(testsDirectory)?.FullName
            ?? throw new InvalidOperationException("Unable to locate the repository root.");

        return Path.Combine(repositoryRoot, first, second, third);
    }
}
