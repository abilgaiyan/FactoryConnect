using System.Net;
using System.Text;
using FactoryConnect.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class SupervisorSecurityHttpTests
{
    [Fact]
    public async Task DisabledFeatureMapsNoSecurityEndpoints()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Services.AddSupervisorSecurity(builder.Configuration);
        await using var app = builder.Build(); app.MapSupervisorSecurity(); await app.StartAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await app.GetTestClient().GetAsync("/api/security/v1/session")).StatusCode);
    }
    [Theory]
    [InlineData("http://factory.test", null, false)]
    [InlineData("https://other.test", null, false)]
    [InlineData("https://factory.test", "https://other.test", false)]
    [InlineData("https://factory.test", "https://factory.test", false)]
    [InlineData("https://factory.test", "https://factory.test", true)]
    public async Task TransportHostOriginCsrfAndForwardedGuardsPrecedeDatabase(string address, string? origin, bool forwarded)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string,string?> { ["SupervisorSecurity:Enabled"] = "true" });
        builder.Services.AddSingleton(new SecurityWebOptions(new Uri("https://factory.test"), "unused", "/unused", "unused", new byte[32]));
        builder.Services.AddSingleton(new SqlSecurityAuthority("invalid-not-contacted", new byte[32], TimeProvider.System));
        builder.Services.AddDataProtection(); builder.Services.AddAntiforgery();
        await using var app = builder.Build(); app.MapSupervisorSecurity(); await app.StartAsync();
        var client = app.GetTestClient(); client.BaseAddress = new Uri(address);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/security/v1/login")
        { Content = new StringContent("{\"username\":\"admin\",\"password\":\"synthetic-password\"}", Encoding.UTF8, "application/json") };
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (forwarded) request.Headers.Add("X-Forwarded-For", "127.0.0.1");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
    }
    [Fact]
    public void PersistentDataProtectionKeysSurviveProviderRecreation()
    {
        var path = Path.Combine(Path.GetTempPath(), "fc-security-keys-" + Guid.NewGuid().ToString("N"));
        try
        {
            var directory = new DirectoryInfo(path);
            var first = DataProtectionProvider.Create(directory).CreateProtector("fixture-purpose");
            var protectedValue = first.Protect("synthetic-session");
            var second = DataProtectionProvider.Create(directory).CreateProtector("fixture-purpose");
            Assert.Equal("synthetic-session", second.Unprotect(protectedValue));
            Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => DataProtectionProvider.Create(directory).CreateProtector("wrong-purpose").Unprotect(protectedValue));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }
}
