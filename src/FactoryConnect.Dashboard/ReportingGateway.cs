using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace FactoryConnect.Dashboard;

public sealed class ReportingGateway(
    IHttpClientFactory httpClientFactory,
    IOptions<DashboardOptions> options)
{
    public const string ClientName = "FactoryConnect.ReportingGateway";

    private readonly DashboardOptions dashboardOptions = options.Value;

    public async Task ForwardAsync(HttpContext context, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        using var timeoutCancellation = new CancellationTokenSource(dashboardOptions.RequestTimeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted,
            timeoutCancellation.Token);

        try
        {
            using var request = CreateUpstreamRequest(context, relativePath);
            using var response = await httpClientFactory
                .CreateClient(ClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCancellation.Token)
                .ConfigureAwait(false);

            context.Response.StatusCode = (int)response.StatusCode;
            if (response.Content is not { } responseContent)
            {
                return;
            }

            if (responseContent.Headers.ContentType is not null)
            {
                context.Response.ContentType = responseContent.Headers.ContentType.ToString();
            }

            await responseContent
                .CopyToAsync(context.Response.Body, linkedCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
            }
        }
        catch (HttpRequestException)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
            }
        }
        catch (IOException)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
            }
        }
    }

    private HttpRequestMessage CreateUpstreamRequest(HttpContext context, string relativePath)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ComposeUpstreamUri(relativePath))
        {
            Content = new StreamContent(context.Request.BodyReader.AsStream(leaveOpen: true))
        };

        if (!string.IsNullOrWhiteSpace(context.Request.ContentType))
        {
            request.Content.Headers.TryAddWithoutValidation(
                HeaderNames.ContentType,
                context.Request.ContentType);
        }

        if (context.Request.Headers.TryGetValue(HeaderNames.Accept, out var accept))
        {
            request.Headers.TryAddWithoutValidation(HeaderNames.Accept, accept.ToArray());
        }

        return request;
    }

    private Uri ComposeUpstreamUri(string relativePath)
    {
        var baseAddress = new Uri(dashboardOptions.ReportingApiBaseAddress, UriKind.Absolute);
        var normalizedBase = baseAddress.AbsoluteUri.EndsWith('/', StringComparison.Ordinal)
            ? baseAddress
            : new Uri(baseAddress.AbsoluteUri + '/', UriKind.Absolute);

        return new Uri(normalizedBase, relativePath.TrimStart('/'));
    }
}
