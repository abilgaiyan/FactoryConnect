using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace FactoryConnect.Dashboard.Tests;

[Collection(DashboardHostTestGroup.Name)]
public sealed class DashboardOverviewConfigurationTests
{
    private static readonly string[] ExpectedDisplayNames = ["Machine D", "Machine A", "Machine B", "Machine C"];
    private static readonly int[] ExpectedDisplayOrders = [5, 10, 10, 20];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(50)]
    public async Task RuntimeConfigurationPreservesArbitraryConfiguredPopulation(int sourceCount)
    {
        await using var factory = CreateFactory(CreateSources(sourceCount));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/dashboard/config");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        var sources = document.RootElement.GetProperty("sources");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(sourceCount, sources.GetArrayLength());
    }

    [Fact]
    public async Task RuntimeConfigurationOrdersSourcesByConfiguredPresentationOrderWithStableTies()
    {
        var values = BaseOverrides();
        AddSource(values, 0, "00000000-0000-0000-0000-000000000003", "processor-c", "Machine C", "Line 2", 20);
        AddSource(values, 1, "00000000-0000-0000-0000-000000000002", "processor-b", "Machine B", "Line 1", 10);
        AddSource(values, 2, "00000000-0000-0000-0000-000000000001", "processor-a", "Machine A", "Line 1", 10);
        AddSource(values, 3, "00000000-0000-0000-0000-000000000004", "processor-d", "Machine D", null, 5);

        await using var factory = CreateFactory(values);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/dashboard/config");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        var sources = document.RootElement.GetProperty("sources").EnumerateArray().ToArray();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var displayNames = sources
            .Select(static source => source.GetProperty("displayName").GetString() ?? string.Empty)
            .ToArray();
        Assert.Equal(ExpectedDisplayNames, displayNames);
        Assert.Equal(ExpectedDisplayOrders, sources.Select(static source => source.GetProperty("displayOrder").GetInt32()).ToArray());
        Assert.Null(sources[0].GetProperty("groupName").GetString());
        Assert.Equal("Line 1", sources[1].GetProperty("groupName").GetString());
    }

    private static WebApplicationFactory<Program> CreateFactory(Dictionary<string, string?> values) =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(Environments.Development);
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(values));
            });

    private static Dictionary<string, string?> CreateSources(int count)
    {
        var values = BaseOverrides();
        for (var index = 0; index < count; index++)
        {
            AddSource(
                values,
                index,
                GuidFromIndex(index + 1).ToString(),
                $"processor-{index + 1:D2}",
                $"Machine {index + 1:D2}",
                $"Line {(index % 4) + 1}",
                index);
        }

        return values;
    }

    private static Dictionary<string, string?> BaseOverrides() => new(StringComparer.Ordinal)
    {
        ["Dashboard:ReportingApiBaseAddress"] = "http://factory-server:5080",
        ["Dashboard:RequestTimeout"] = "00:00:30"
    };

    private static void AddSource(
        Dictionary<string, string?> values,
        int index,
        string machineId,
        string processorId,
        string displayName,
        string? groupName,
        int displayOrder)
    {
        var prefix = $"Dashboard:Sources:{index}";
        values[$"{prefix}:MachineId"] = machineId;
        values[$"{prefix}:ProcessorId"] = processorId;
        values[$"{prefix}:DisplayName"] = displayName;
        values[$"{prefix}:GroupName"] = groupName;
        values[$"{prefix}:DisplayOrder"] = displayOrder.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Guid GuidFromIndex(int value) =>
        Guid.Parse($"00000000-0000-0000-0000-{value:D12}");
}
