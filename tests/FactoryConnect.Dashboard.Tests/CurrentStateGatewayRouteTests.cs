using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FactoryConnect.Dashboard.Tests;

public sealed class CurrentStateGatewayRouteTests
{
    private const string MachineId = "11111111-1111-1111-1111-111111111111";

    [Theory]
    [InlineData(HttpStatusCode.OK, "application/json")]
    [InlineData(HttpStatusCode.BadRequest, "application/problem+json")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "application/problem+json")]
    public async Task ExactCurrentStateGetPreservesUpstreamResponse(
        HttpStatusCode status,
        string contentType)
    {
        var responseBytes = """{"outcome":"no-evidence"}"""u8.ToArray();
        HttpMethod? observedMethod = null;
        string? observedPath = null;
        using var upstream = new StubHttpClientFactory((request, _) =>
        {
            observedMethod = request.Method;
            observedPath = request.RequestUri?.AbsolutePath;
            var response = new HttpResponseMessage(status)
            {
                Content = new ByteArrayContent(responseBytes)
            };
            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            return Task.FromResult(response);
        });
        await using var factory = CreateFactory(upstream);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/machines/v1/{MachineId}/current-state");

        Assert.Equal(HttpMethod.Get, observedMethod);
        Assert.Equal($"/factoryconnect/api/machines/v1/{MachineId}/current-state", observedPath);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(responseBytes, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task NonGetCurrentStateRouteIsNotForwarded()
    {
        using var upstream = new StubHttpClientFactory((_, _) =>
            throw new InvalidOperationException("The upstream must not be called."));
        await using var factory = CreateFactory(upstream);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            $"/api/machines/v1/{MachineId}/current-state",
            new StringContent("{}"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static WebApplicationFactory<Program> CreateFactory(IHttpClientFactory upstream) =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(Environments.Development);
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Dashboard:ReportingApiBaseAddress"] = "http://factory-server:5080/factoryconnect/",
                        ["Dashboard:RequestTimeout"] = "00:00:30",
                        ["Dashboard:Sources:0:MachineId"] = MachineId,
                        ["Dashboard:Sources:0:ProcessorId"] = "processor-1",
                        ["Dashboard:Sources:0:SiteId"] = "site-1",
                        ["Dashboard:Sources:0:ProductionLineId"] = "line-1",
                        ["Dashboard:Sources:0:DisplayName"] = "Machine 1",
                        ["Dashboard:Sources:0:DisplayOrder"] = "0"
                    }));
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton(upstream);
                    services.AddSingleton<IHttpClientFactory>(upstream);
                });
            });
    private sealed class StubHttpClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient client;

        public StubHttpClientFactory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        {
            client = new HttpClient(new StubHandler(send)) { Timeout = Timeout.InfiniteTimeSpan };
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
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
