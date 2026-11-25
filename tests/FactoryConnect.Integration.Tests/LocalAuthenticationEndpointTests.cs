using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using FactoryConnect.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;

namespace FactoryConnect.Integration.Tests;

public sealed class LocalAuthenticationEndpointTests
{
    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("Supervisor", HttpStatusCode.Forbidden)]
    [InlineData("Manager", HttpStatusCode.Forbidden)]
    [InlineData("Administrator", HttpStatusCode.ServiceUnavailable)]
    public async Task UserAdministrationRequiresAdministrator(string? role, HttpStatusCode expected)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PersistenceProviders:SqlServer:ConnectionString"] = ""
        });
        builder.Services.AddLocalAuthentication();
        await using var app = builder.Build();
        app.UseRouting();
        app.UseRateLimiter();
        app.UseAuthentication();
        // Inject only the test identity; the production authorization policies and
        // endpoint handlers remain unchanged. SQL lifecycle is tested separately.
        app.Use(async (context, next) =>
        {
            if (role is not null)
                context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Role, role)
                }, "Test"));
            await next(context);
        });
        app.UseAuthorization();
        app.MapLocalAuthentication();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var list = await client.GetAsync("/api/auth/v1/users");
        Assert.Equal(expected, list.StatusCode);
        var id = Guid.NewGuid();
        foreach (var (method, path, action, body) in new (HttpMethod, string, string, object)[]
        {
            (HttpMethod.Post, "/api/auth/v1/users", "create-user", new { username = "test-user", password = "Test-password-123", role = "Supervisor" }),
            (HttpMethod.Put, $"/api/auth/v1/users/{id:D}/access", "change-access", new { role = "Manager", isActive = true }),
            (HttpMethod.Put, $"/api/auth/v1/users/{id:D}/password", "reset-password", new { password = "Test-password-456" })
        })
        {
            using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
            request.Headers.Add("X-FC-Request", action);
            using var response = await client.SendAsync(request);
            Assert.Equal(expected, response.StatusCode);
            if (role == "Administrator")
            {
                using var missingHeader = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
                using var rejected = await client.SendAsync(missingHeader);
                Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            }
        }
    }
}
