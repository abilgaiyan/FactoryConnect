using System.Security.Claims;
using System.Net;
using System.Net.Http.Json;
using FactoryConnect.Api.Machines;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.Integration.Tests;

public sealed class MachineEvidenceProfileEndpointTests
{
    private static readonly Guid Machine = Guid.Parse("d43a4e45-0e77-4bb8-90df-1251f7c4b290");
    private static readonly string Path = $"/api/commissioning/v1/machines/{Machine:D}/evidence-profiles";

    [Theory]
    [InlineData(false, false, HttpStatusCode.Forbidden)]
    [InlineData(true, false, HttpStatusCode.Forbidden)]
    [InlineData(true, true, HttpStatusCode.ServiceUnavailable)]
    public async Task WritesRequireLocalCommissioningOptInAndConfiguredWriter(
        bool commissioning, bool writes, HttpStatusCode expected)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Commissioning:Enabled"] = commissioning.ToString(),
            ["MachineEvidenceProfileLocal:EnableWrites"] = writes.ToString()
        });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("OperationalRead", policy => policy.RequireRole("Administrator", "Supervisor", "Manager"));
            options.AddPolicy("AdministratorWrite", policy => policy.RequireRole("Administrator"));
        });
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, Machine.ToString()),
                    new Claim(ClaimTypes.Role, "Administrator") }, "Test"));
            await next(context);
        });
        app.UseAuthorization();
        app.MapMachineEvidenceProfileEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync(Path, new
        {
            profileRevisionId = Guid.NewGuid(), revisionNumber = 1,
            evidenceMode = "StateOnly", stateDataItemId = "execution",
            effectiveFromUtc = new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero)
        });
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task ReadRequiresLocalCommissioningAndNeverFallsBackToFabricatedProfile()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Commissioning:Enabled"] = "true"
        });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("OperationalRead", policy => policy.RequireRole("Administrator", "Supervisor", "Manager"));
            options.AddPolicy("AdministratorWrite", policy => policy.RequireRole("Administrator"));
        });
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, Machine.ToString()),
                    new Claim(ClaimTypes.Role, "Administrator") }, "Test"));
            await next(context);
        });
        app.UseAuthorization();
        app.MapMachineEvidenceProfileEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync(Path);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var effective = await client.GetAsync(
            $"/api/commissioning/v1/machines/{Machine:D}/evidence-profile?atUtc=2026-11-20T00:00:00%2B00:00");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, effective.StatusCode);
        using var forwarded = new HttpRequestMessage(HttpMethod.Get, Path);
        forwarded.Headers.TryAddWithoutValidation("X-Forwarded-For", "127.0.0.1");
        using var blocked = await client.SendAsync(forwarded);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
    }
}
