using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace FactoryConnect.Dashboard.Tests;

[Collection(DashboardHostTestGroup.Name)]
public sealed class DashboardWholeFeatureConformanceTests
{
    private const string ShiftPath = "/api/reporting/v1/operational-metrics/shifts/query";
    private const string ProductionDayPath = "/api/reporting/v1/operational-metrics/production-days/query";

    [Fact]
    public async Task SevenSourceLanCompositionProjectsEveryConfiguredIdentityWithoutUpstreamAddress()
    {
        await using var factory = CreateFactory(SevenSourceOverrides(), Environments.Production);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/dashboard/config");
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var sources = root.GetProperty("sources").EnumerateArray().ToArray();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/", root.GetProperty("reportingBasePath").GetString());
        Assert.Equal(30000, root.GetProperty("requestTimeoutMilliseconds").GetInt32());
        Assert.Equal(7, sources.Length);

        for (var index = 0; index < sources.Length; index++)
        {
            var sourceNumber = index + 1;
            Assert.Equal(
                $"00000000-0000-0000-0000-{sourceNumber:000000000000}",
                sources[index].GetProperty("machineId").GetString());
            Assert.Equal(
                $"operational-metrics-{sourceNumber}",
                sources[index].GetProperty("processorId").GetString());
            Assert.Equal(
                $"Machine {sourceNumber}",
                sources[index].GetProperty("displayName").GetString());
        }

        Assert.DoesNotContain("factory-reporting.internal:5080", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reportingApiBaseAddress", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(ShiftPath)]
    [InlineData(ProductionDayPath)]
    public async Task ExactReportingRoutesRejectEveryNonPostMethod(string path)
    {
        await using var factory = CreateFactory(SevenSourceOverrides(), Environments.Production);
        using var client = factory.CreateClient();

        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
        {
            using var request = new HttpRequestMessage(method, path);
            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("/api/reporting/v1/operational-metrics/shifts/query/")]
    [InlineData("/api/reporting/v1/operational-metrics/production-days/query/")]
    [InlineData("/api/reporting/v1/operational-metrics/shifts")]
    [InlineData("/api/reporting/v1/operational-metrics/production-days")]
    [InlineData("/api/reporting/v1/operational-metrics/query")]
    public async Task NearMissReportingPathsAreNeverGatewayRoutes(string path)
    {
        await using var factory = CreateFactory(SevenSourceOverrides(), Environments.Production);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent("{}")
        };

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void DashboardAssemblyRetainsPresentationOnlyFactoryConnectBoundary()
    {
        var factoryConnectReferences = typeof(Program).Assembly
            .GetReferencedAssemblies()
            .Select(static assembly => assembly.Name)
            .Where(static name => name?.StartsWith("FactoryConnect.", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.Empty(factoryConnectReferences);
    }

    [Fact]
    public async Task DashboardProjectHasNoFactoryConnectProjectReferences()
    {
        var repositoryRoot = FindRepositoryRoot();
        var projectPath = Path.Combine(
            repositoryRoot,
            "src",
            "FactoryConnect.Dashboard",
            "FactoryConnect.Dashboard.csproj");
        var project = await File.ReadAllTextAsync(projectPath);

        Assert.DoesNotContain("<ProjectReference", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FactoryConnect.Core", project, StringComparison.Ordinal);
        Assert.DoesNotContain("FactoryConnect.Edge", project, StringComparison.Ordinal);
        Assert.DoesNotContain("FactoryConnect.Persistence", project, StringComparison.Ordinal);
        Assert.DoesNotContain("FactoryConnect.Api", project, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepositoryDashboardDefaultsRemainFailClosed()
    {
        var repositoryRoot = FindRepositoryRoot();
        var settingsPath = Path.Combine(
            repositoryRoot,
            "src",
            "FactoryConnect.Dashboard",
            "appsettings.json");
        var settings = await File.ReadAllTextAsync(settingsPath);
        using var document = JsonDocument.Parse(settings);
        var dashboard = document.RootElement.GetProperty("Dashboard");

        Assert.Equal(string.Empty, dashboard.GetProperty("ReportingApiBaseAddress").GetString());
        Assert.Empty(dashboard.GetProperty("Sources").EnumerateArray());
    }

    private static WebApplicationFactory<Program> CreateFactory(
        IDictionary<string, string?> values,
        string environment) =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(environment);
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(values));
            });

    private static Dictionary<string, string?> SevenSourceOverrides()
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Dashboard:ReportingApiBaseAddress"] = "http://factory-reporting.internal:5080/factoryconnect/",
            ["Dashboard:RequestTimeout"] = "00:00:30"
        };

        for (var index = 0; index < 7; index++)
        {
            var sourceNumber = index + 1;
            values[$"Dashboard:Sources:{index}:MachineId"] =
                $"00000000-0000-0000-0000-{sourceNumber:000000000000}";
            values[$"Dashboard:Sources:{index}:ProcessorId"] = $"operational-metrics-{sourceNumber}";
            values[$"Dashboard:Sources:{index}:DisplayName"] = $"Machine {sourceNumber}";
        }

        return values;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FactoryConnect.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("FactoryConnect repository root could not be located.");
    }
}
