using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace FactoryConnect.Dashboard.Tests;

public sealed class DashboardHostTests
{
    [Fact]
    public async Task ValidConfigurationStartsAndServesLivenessReadinessAndPlaceholder()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var live = await client.GetAsync("/health/live");
        using var ready = await client.GetAsync("/health/ready");
        using var root = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        Assert.Contains("data-fc-dashboard-placeholder", await root.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClientSideRouteFallsBackToIndex()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/production-days/2026-08-30");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("data-fc-dashboard-placeholder", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
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
        Assert.DoesNotContain("data-fc-dashboard-placeholder", content, StringComparison.Ordinal);
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
        yield return Case("Dashboard:Sources:0:DisplayName", " ");
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
    public void EmptySourcesFailDuringStartup()
    {
        var overrides = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Dashboard:ReportingApiBaseAddress"] = "http://factory-server:5080",
            ["Dashboard:RequestTimeout"] = "00:00:30"
        };

        using var factory = CreateFactory(overrides, Environments.Production);

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Dashboard:Sources", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateSourceIdentityFailsDuringStartup()
    {
        var overrides = ValidOverrides();
        overrides["Dashboard:Sources:1:MachineId"] = overrides["Dashboard:Sources:0:MachineId"];
        overrides["Dashboard:Sources:1:ProcessorId"] = overrides["Dashboard:Sources:0:ProcessorId"];
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
        ["Dashboard:Sources:0:DisplayName"] = "Machine 1"
    };

    private static object[] Case(string key, string? value) => [key, value ?? string.Empty];
}
