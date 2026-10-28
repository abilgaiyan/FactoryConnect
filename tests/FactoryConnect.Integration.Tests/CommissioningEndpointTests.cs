using System.Net;
using System.Text.Json;
using FactoryConnect.Api.Commissioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.Integration.Tests;

public sealed class CommissioningEndpointTests
{
    [Theory]
    [InlineData(false, HttpStatusCode.NotFound)]
    [InlineData(true, HttpStatusCode.OK)]
    public async Task GetIsOptInAndNeverInventsMissingAuthority(bool enabled, HttpStatusCode expected)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Commissioning:Enabled"] = enabled.ToString(),
        });
        builder.Services.AddSingleton<CommissioningSnapshotReader>();
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            await next(context);
        });
        app.MapCommissioning();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/api/commissioning/v1/snapshot");
        Assert.Equal(expected, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        if (enabled)
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("unavailable", document.RootElement.GetProperty("durable").GetProperty("status").GetString());
            Assert.Equal("unknown/unverified", document.RootElement.GetProperty("runtimeActivation").GetString());
            Assert.Empty(document.RootElement.GetProperty("durable").GetProperty("processors").EnumerateArray());
        }
        using var post = await client.PostAsync("/api/commissioning/v1/snapshot", null);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
    }
}
