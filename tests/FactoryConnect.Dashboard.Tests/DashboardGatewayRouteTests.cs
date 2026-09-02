using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FactoryConnect.Dashboard.Tests;

public sealed class DashboardGatewayRouteTests
{
    [Theory]
    [InlineData(
        "/api/reporting/v1/operational-metrics/shifts/query",
        "/factoryconnect/api/reporting/v1/operational-metrics/shifts/query")]
    [InlineData(
        "/api/reporting/v1/operational-metrics/production-days/query",
        "/factoryconnect/api/reporting/v1/operational-metrics/production-days/query")]
    [InlineData(
        "/api/reporting/v1/operational-metrics/production-day-shifts/query",
        "/factoryconnect/api/reporting/v1/operational-metrics/production-day-shifts/query")]
    public async Task ExactReportingRouteForwardsToConfiguredUpstreamPath(
        string dashboardPath,
        string expectedUpstreamPath)
    {
        var requestBytes = Encoding.UTF8.GetBytes("{\"sources\":[]}");
        var responseBytes = Encoding.UTF8.GetBytes("{\"items\":[]}");
        string? observedPath = null;
        byte[]? observedBytes = null;

        using var upstream = new StubHttpClientFactory(async (request, cancellationToken) =>
        {
            observedPath = request.RequestUri?.AbsolutePath;
            observedBytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            var response = new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new ByteArrayContent(responseBytes)
            };
            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
            return response;
        });
        await using var factory = CreateFactory(upstream);
        using var client = factory.CreateClient();
        using var content = new ByteArrayContent(requestBytes);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");

        using var response = await client.PostAsync(dashboardPath, content);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(expectedUpstreamPath, observedPath);
        Assert.Equal(requestBytes, observedBytes);
        Assert.Equal(responseBytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ProductionDayShiftRoutePreservesProblemDetailsResponse()
    {
        const string dashboardPath = "/api/reporting/v1/operational-metrics/production-day-shifts/query";
        var responseBytes = Encoding.UTF8.GetBytes("{\"type\":\"urn:factoryconnect:problem:reporting:production-day-shift-roster-coverage-required\",\"status\":409}");

        using var upstream = new StubHttpClientFactory((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new ByteArrayContent(responseBytes)
            };
            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/problem+json");
            return Task.FromResult(response);
        });
        await using var factory = CreateFactory(upstream);
        using var client = factory.CreateClient();
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(dashboardPath, content);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(responseBytes, await response.Content.ReadAsByteArrayAsync());
    }

    private static WebApplicationFactory<Program> CreateFactory(IHttpClientFactory upstream) =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(Environments.Development);
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["Dashboard:ReportingApiBaseAddress"] = "http://factory-server:5080/factoryconnect/",
                        ["Dashboard:RequestTimeout"] = "00:00:30",
                        ["Dashboard:Sources:0:MachineId"] = "11111111-1111-1111-1111-111111111111",
                        ["Dashboard:Sources:0:ProcessorId"] = "operational-metrics",
                        ["Dashboard:Sources:0:SiteId"] = "plant-1",
                        ["Dashboard:Sources:0:ProductionLineId"] = "line-1",
                        ["Dashboard:Sources:0:DisplayName"] = "Machine 1"
                    }));
                builder.ConfigureServices(services => services.AddSingleton(upstream));
            });

    private sealed class StubHttpClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient client;

        public StubHttpClientFactory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        {
            client = new HttpClient(new StubHandler(send))
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
        }

        public HttpClient CreateClient(string name)
        {
            Assert.Equal(ReportingGateway.ClientName, name);
            return client;
        }

        public void Dispose() => client.Dispose();
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
