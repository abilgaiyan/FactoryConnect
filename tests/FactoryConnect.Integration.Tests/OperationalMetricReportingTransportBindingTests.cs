using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FactoryConnect.Integration.Tests;

public sealed class OperationalMetricReportingTransportBindingTests
{
    private const string ShiftRoute =
        "/api/reporting/v1/operational-metrics/shifts/query";
    private const string InvalidRequestType =
        "urn:factoryconnect:problem:reporting:invalid-request";

    [Fact]
    public async Task EmptyBodyReturnsStableProblemDetails()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, ShiftRoute);

        using var response = await client.SendAsync(request);

        await AssertInvalidRequestAsync(response);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"sources\":\"not-an-array\"}")]
    public async Task InvalidJsonTransportBodyReturnsStableProblemDetails(string body)
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, ShiftRoute)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request);

        await AssertInvalidRequestAsync(response);
    }

    private static async Task AssertInvalidRequestAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.Equal(InvalidRequestType, root.GetProperty("type").GetString());
        Assert.Equal("Invalid reporting query", root.GetProperty("title").GetString());
        Assert.Equal(400, root.GetProperty("status").GetInt32());
        Assert.Equal(
            "invalid-reporting-query",
            root.GetProperty("code").GetString());
        Assert.False(root.TryGetProperty("exception", out _));
        Assert.False(root.TryGetProperty("stackTrace", out _));
    }
}
