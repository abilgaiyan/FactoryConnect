using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace FactoryConnect.Dashboard.Tests;

[Collection(DashboardHostTestGroup.Name)]
public sealed class DashboardHostTests
{
    [Fact]
    public async Task ValidConfigurationStartsAndServesLivenessReadinessAndProductionShell()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var live = await client.GetAsync("/health/live");
        using var ready = await client.GetAsync("/health/ready");
        using var root = await client.GetAsync("/");
        var content = await root.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        Assert.Contains("<div id=\"root\"></div>", content, StringComparison.Ordinal);
        Assert.Contains("/assets/index-", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClientSideRouteFallsBackToProductionShell()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/production-days/2026-08-30");
        var content = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<div id=\"root\"></div>", content, StringComparison.Ordinal);
        Assert.Contains("/assets/index-", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HeadClientSideRouteUsesSpaFallbackWithoutResponseBody()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Head, "/production-days/2026-08-30");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task NonNavigationMethodsNeverReceiveSpaShell(string method)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), "/production-days/2026-08-30");

        using var response = await client.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("<div id=\"root\"></div>", content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/health/unknown")]
    [InlineData("/api/future")]
    [InlineData("/config/runtime")]
    [InlineData("/configuration/runtime")]
    [InlineData("/missing.js")]
    public async Task ReservedAndMissingStaticPathsDoNotFallThroughToSpa(string path)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void DashboardAssemblyHasNoProhibitedFactoryConnectReferences()
    {
        var prohibited = new HashSet<string>(StringComparer.Ordinal)
        {
            "FactoryConnect.Abstractions",
            "FactoryConnect.Api",
            "FactoryConnect.Core",
            "FactoryConnect.Edge",
            "FactoryConnect.Infrastructure",
            "FactoryConnect.Persistence",
            "FactoryConnect.Persistence.SqlServer",
            "FactoryConnect.Protocols.MTConnect",
            "FactoryConnect.Protocols.Modbus"
        };

        var referenced = typeof(Program).Assembly
            .GetReferencedAssemblies()
            .Select(static assembly => assembly.Name)
            .Where(static name => name is not null)
            .Cast<string>();

        Assert.DoesNotContain(referenced, prohibited.Contains);
    }

    public static IEnumerable<object[]> InvalidConfigurations()
    {
        yield return Case("Dashboard:ReportingApiBaseAddress", "relative/reporting");
        yield return Case("Dashboard:ReportingApiBaseAddress", "ftp://factory-server");
        yield return Case("Dashboard:RequestTimeout", "00:00:00");
        yield return Case("Dashboard:RequestTimeout", "00:05:00.001");
        yield return Case("Dashboard:Sources:0:MachineId", Guid.Empty.ToString());
        yield return Case("Dashboard:Sources:0:ProcessorId", " ");
        yield return Case("Dashboard:Sources:0:ProcessorId", " operational-metrics");
        yield return Case("Dashboard:Sources:0:ProcessorId", "operational-metrics ");
        yield return Case("Dashboard:Sources:0:SiteId", " ");
        yield return Case("Dashboard:Sources:0:SiteId", " plant-1");
        yield return Case("Dashboard:Sources:0:SiteId", "plant-1 ");
        yield return Case("Dashboard:Sources:0:ProductionLineId", " ");
        yield return Case("Dashboard:Sources:0:ProductionLineId", " line-1");
        yield return Case("Dashboard:Sources:0:ProductionLineId", "line-1 ");
        yield return Case("Dashboard:Sources:0:DisplayName", " ");
        yield return Case("Dashboard:Sources:0:DisplayName", " Machine 1");
        yield return Case("Dashboard:Sources:0:DisplayName", "Machine 1 ");
        yield return Case("Dashboard:Sources:0:GroupName", " ");
        yield return Case("Dashboard:Sources:0:GroupName", " Line 1");
        yield return Case("Dashboard:Sources:0:GroupName", "Line 1 ");
        yield return Case("Dashboard:Sources:0:DisplayOrder", "-1");
    }

    [Theory]
    [MemberData(nameof(InvalidConfigurations))]
    public void InvalidConfigurationFailsDuringStartup(string key, string? value)
    {
        var overrides = ValidOverrides();
        overrides[key] = value;

        using var factory = CreateFactory(overrides);

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Dashboard", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptySourcesAreValidConfiguredFactoryState()
    {
        var overrides = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Dashboard:ReportingApiBaseAddress"] = "http://factory-server:5080",
            ["Dashboard:RequestTimeout"] = "00:00:30"
        };

        await using var factory = CreateFactory(overrides, Environments.Production);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/dashboard/config");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"sources\":[]", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateSourceIdentityFailsDuringStartup()
    {
        var overrides = ValidOverrides();
        overrides["Dashboard:Sources:1:MachineId"] = overrides["Dashboard:Sources:0:MachineId"];
        overrides["Dashboard:Sources:1:ProcessorId"] = overrides["Dashboard:Sources:0:ProcessorId"];
        overrides["Dashboard:Sources:1:SiteId"] = "plant-2";
        overrides["Dashboard:Sources:1:ProductionLineId"] = "line-2";
        overrides["Dashboard:Sources:1:DisplayName"] = "Duplicate";

        using var factory = CreateFactory(overrides);

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("duplicate source", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProductionLoopbackReportingApiFailsDuringStartup()
    {
        var overrides = ValidOverrides();
        overrides["Dashboard:ReportingApiBaseAddress"] = "http://localhost:5080";

        using var factory = CreateFactory(overrides, Environments.Production);

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("loopback", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static WebApplicationFactory<Program> CreateFactory(
        IDictionary<string, string?>? overrides = null,
        string? environment = null)
    {
        var values = overrides ?? ValidOverrides();
        var effectiveEnvironment = environment ?? Environments.Development;
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(effectiveEnvironment);
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(values));
            });
    }

    private static Dictionary<string, string?> ValidOverrides() => new(StringComparer.Ordinal)
    {
        ["Dashboard:ReportingApiBaseAddress"] = "http://factory-server:5080",
        ["Dashboard:RequestTimeout"] = "00:00:30",
        ["Dashboard:Sources:0:MachineId"] = "11111111-1111-1111-1111-111111111111",
        ["Dashboard:Sources:0:ProcessorId"] = "operational-metrics",
        ["Dashboard:Sources:0:SiteId"] = "plant-1",
        ["Dashboard:Sources:0:ProductionLineId"] = "line-1",
        ["Dashboard:Sources:0:DisplayName"] = "Machine 1",
        ["Dashboard:Sources:0:GroupName"] = "Presentation Group A",
        ["Dashboard:Sources:0:DisplayOrder"] = "10"
    };

    private static object[] Case(string key, string? value) => [key, value ?? string.Empty];
}
