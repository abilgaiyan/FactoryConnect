using System.Runtime.CompilerServices;
using System.Text.Json;
using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Core.Machines;
using FactoryConnect.Edge;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        var path = RepositoryPath("src", "FactoryConnect.Edge", "appsettings.json");

        AssertMaximumCurrentAge(path);
        AssertMtConnectActivityStreamAuthority(
            path,
            CheckedInDeviceKey,
            CheckedInActivityStreamKey);
    }

    [Fact]
    public void CheckedInRuntimeConfigurationReachesSuccessfulRootComposition()
    {
        var path = RepositoryPath("src", "FactoryConnect.Edge", "appsettings.json");
        var configuration = new ConfigurationBuilder()
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
