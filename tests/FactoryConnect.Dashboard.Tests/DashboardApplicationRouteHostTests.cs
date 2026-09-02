using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace FactoryConnect.Dashboard.Tests;

[Collection(DashboardHostTestGroup.Name)]
public sealed class DashboardApplicationRouteHostTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/production-days/2026-08-30")]
    [InlineData("/machines/11111111-1111-1111-1111-111111111111")]
    [InlineData("/production-days/2026-08-30/report")]
    public async Task ApplicationRoutesServeProductionFrontendEntry(string path)
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(ValidOverrides()));
            });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<div id=\"root\"></div>", html, StringComparison.Ordinal);
        Assert.Contains("/assets/", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-fc-dashboard-placeholder", html, StringComparison.Ordinal);
    }

    private static Dictionary<string, string?> ValidOverrides() => new(StringComparer.Ordinal)
    {
        ["Dashboard:ReportingApiBaseAddress"] = "http://factory-server:5080",
        ["Dashboard:RequestTimeout"] = "00:00:30",
        ["Dashboard:Sources:0:MachineId"] = "11111111-1111-1111-1111-111111111111",
        ["Dashboard:Sources:0:ProcessorId"] = "operational-metrics",
        ["Dashboard:Sources:0:SiteId"] = "plant-1",
        ["Dashboard:Sources:0:DisplayName"] = "Machine 1"
    };
}
