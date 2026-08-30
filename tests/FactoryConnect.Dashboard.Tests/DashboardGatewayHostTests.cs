using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace FactoryConnect.Dashboard.Tests;

public sealed class DashboardGatewayHostTests
{
    [Fact]
    public async Task RuntimeConfigurationProjectsOnlyBrowserSafeDeploymentValues()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/dashboard/config");
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/", root.GetProperty("reportingBasePath").GetString());
        Assert.Equal(30000, root.GetProperty("requestTimeoutMilliseconds").GetInt32());
        var source = Assert.Single(root.GetProperty("sources").EnumerateArray());
        Assert.Equal("11111111-1111-1111-1111-111111111111", source.GetProperty("machineId").GetString());
        Assert.Equal("operational-metrics", source.GetProperty("processorId").GetString());
        Assert.Equal("Machine 1", source.GetProperty("displayName").GetString());
        Assert.DoesNotContain("factory-server:5080", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reportingApiBaseAddress", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/api/reporting/v1/operational-metrics/unknown/query")]
    [InlineData("/api/reporting/v1/operational-metrics/shifts/query/extra")]
    public async Task ArbitraryReportingPathsAreNotProxied(string path)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent("{}")
        };

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/dashboard/unknown")]
    [InlineData("/api/reporting/v1/operational-metrics/shifts/query")]
    public async Task InfrastructurePathsNeverFallThroughToSpa(string path)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);
        var content = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("data-fc-dashboard-placeholder", content, StringComparison.Ordinal);
    }

    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(Environments.Development);
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["Dashboard:ReportingApiBaseAddress"] = "http://factory-server:5080",
                        ["Dashboard:RequestTimeout"] = "00:00:30",
                        ["Dashboard:Sources:0:MachineId"] = "11111111-1111-1111-1111-111111111111",
                        ["Dashboard:Sources:0:ProcessorId"] = "operational-metrics",
                        ["Dashboard:Sources:0:DisplayName"] = "Machine 1"
                    }));
            });
}
