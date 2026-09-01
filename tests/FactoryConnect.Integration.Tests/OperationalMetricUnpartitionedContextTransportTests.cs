using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FactoryConnect.Integration.Tests;

public sealed class OperationalMetricUnpartitionedContextTransportTests
{
    private const string ProductionDayRoute =
        "/api/reporting/v1/operational-metrics/production-days/query";

    [Fact]
    public async Task UnpartitionedOnlyCannotBeCombinedWithContextIdentityOverHttp()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var body = new
        {
            sources = new[]
            {
                new
                {
                    machineId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
                    processorId = "metrics-v1",
                },
            },
            fromInclusive = "2026-08-31",
            toExclusive = "2026-09-01",
            metrics = new[]
            {
                new { metricKey = "OEE", version = "1.0" },
            },
            context = new
            {
                productionOrderId = (string?)null,
                operationId = (string?)null,
                partId = "part-1",
                operatorId = (string?)null,
                unpartitionedOnly = true,
            },
            statuses = (string[]?)null,
            order = "period-ascending",
            pageSize = 200,
            continuationToken = (string?)null,
        };

        using var response = await client.PostAsJsonAsync(ProductionDayRoute, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "invalid-reporting-query",
            document.RootElement.GetProperty("code").GetString());
    }
}
