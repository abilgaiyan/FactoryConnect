using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FactoryConnect.Dashboard;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace FactoryConnect.Dashboard.Tests;

public sealed class ReportingGatewayTests
{
    private const string ShiftPath = "api/reporting/v1/operational-metrics/shifts/query";

    [Fact]
    public async Task ForwardingPreservesBasePathRequestBytesAndResponse()
    {
        var requestBytes = Encoding.UTF8.GetBytes("{ \"sources\" : [ ] }");
        var responseBytes = Encoding.UTF8.GetBytes("{\"items\":[],\"continuationToken\":null}");
        Uri? observedUri = null;
        byte[]? observedRequestBytes = null;
        string? observedContentType = null;

        using var factory = new StubHttpClientFactory(async (request, cancellationToken) =>
        {
            observedUri = request.RequestUri;
            observedRequestBytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            observedContentType = request.Content.Headers.ContentType?.ToString();

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(responseBytes)
            };
            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json; charset=utf-8");
            return response;
        });
        var gateway = CreateGateway(factory, "http://factory-server:5080/factoryconnect/");
        var context = CreateContext(requestBytes, "application/json; charset=utf-8");

        await gateway.ForwardAsync(context, ShiftPath);

        Assert.Equal("http://factory-server:5080/factoryconnect/api/reporting/v1/operational-metrics/shifts/query", observedUri?.AbsoluteUri);
        Assert.Equal(requestBytes, observedRequestBytes);
        Assert.Equal("application/json; charset=utf-8", observedContentType);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);
        Assert.Equal(responseBytes, ReadResponseBytes(context));
    }

    [Fact]
    public async Task ProblemDetailsBodyIsPreservedWithoutClassification()
    {
        var problemBytes = Encoding.UTF8.GetBytes("{\"type\":\"urn:factoryconnect:problem:reporting:invalid-request\",\"code\":\"invalid-reporting-query\"}");

        using var factory = new StubHttpClientFactory((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new ByteArrayContent(problemBytes)
            };
            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/problem+json");
            return Task.FromResult(response);
        });
        var gateway = CreateGateway(factory);
        var context = CreateContext(Encoding.UTF8.GetBytes("{}"), "application/json");

        await gateway.ForwardAsync(context, ShiftPath);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
        Assert.Equal(problemBytes, ReadResponseBytes(context));
    }

    [Fact]
    public async Task UpstreamTimeoutBecomesGatewayTimeoutWithoutReportingBody()
    {
        using var factory = new StubHttpClientFactory(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var gateway = CreateGateway(factory, timeout: TimeSpan.FromMilliseconds(20));
        var context = CreateContext(Encoding.UTF8.GetBytes("{}"), "application/json");

        await gateway.ForwardAsync(context, ShiftPath);

        Assert.Equal(StatusCodes.Status504GatewayTimeout, context.Response.StatusCode);
        Assert.Empty(ReadResponseBytes(context));
    }

    [Fact]
    public async Task UpstreamNetworkFailureBecomesBadGatewayWithoutReportingBody()
    {
        using var factory = new StubHttpClientFactory((_, _) =>
            throw new HttpRequestException("upstream unavailable"));
        var gateway = CreateGateway(factory);
        var context = CreateContext(Encoding.UTF8.GetBytes("{}"), "application/json");

        await gateway.ForwardAsync(context, ShiftPath);

        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
        Assert.Empty(ReadResponseBytes(context));
    }

    [Fact]
    public async Task BrowserCancellationAbortsUpstreamWithoutSynthesizingResponse()
    {
        using var requestCancellation = new CancellationTokenSource();
        using var factory = new StubHttpClientFactory(async (_, cancellationToken) =>
        {
            requestCancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var gateway = CreateGateway(factory);
        var context = CreateContext(Encoding.UTF8.GetBytes("{}"), "application/json");
        context.RequestAborted = requestCancellation.Token;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gateway.ForwardAsync(context, ShiftPath));

        Assert.Empty(ReadResponseBytes(context));
    }

    private static ReportingGateway CreateGateway(
        IHttpClientFactory factory,
        string baseAddress = "http://factory-server:5080",
        TimeSpan? timeout = null) =>
        new(
            factory,
            Options.Create(new DashboardOptions
            {
                ReportingApiBaseAddress = baseAddress,
                RequestTimeout = timeout ?? TimeSpan.FromSeconds(30),
                Sources =
                [
                    new DashboardSourceOptions
                    {
                        MachineId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                        ProcessorId = "operational-metrics",
                        DisplayName = "Machine 1"
                    }
                ]
            }));

    private static DefaultHttpContext CreateContext(byte[] body, string contentType)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = contentType;
        context.Request.Body = new MemoryStream(body, writable: false);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static byte[] ReadResponseBytes(HttpContext context)
    {
        var stream = Assert.IsType<MemoryStream>(context.Response.Body);
        return stream.ToArray();
    }

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
