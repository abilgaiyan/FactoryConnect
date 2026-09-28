using System.Runtime.CompilerServices;
using System.Text.Json;
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

    [Fact]
    public void CheckedInRuntimeConfigurationCarriesCommissioningFreshnessAuthority()
    {
        AssertMaximumCurrentAge(
            RepositoryPath("src", "FactoryConnect.Edge", "appsettings.json"));
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
        AssertMaximumCurrentAge(
            RepositoryPath("config", "templates", "edge.production.template.json"));
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
