using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FactoryConnect.Integration.Tests;

// Explicit opt-in for business-logic tests; production authentication is unchanged.
internal static class ReportingTestAuthentication
{
    internal static WebApplicationFactory<Program> WithReportingIdentity(this WebApplicationFactory<Program> factory) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(options =>
            {
                options.DefaultScheme = "ReportingTest";
                options.DefaultAuthenticateScheme = "ReportingTest";
                options.DefaultChallengeScheme = "ReportingTest";
                options.DefaultForbidScheme = "ReportingTest";
            }).AddScheme<AuthenticationSchemeOptions, ReportingTestHandler>("ReportingTest", _ => { });
        }));
}

internal sealed class ReportingTestHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "11111111-1111-1111-1111-111111111111"),
                new Claim(ClaimTypes.Name, "reporting-test"),
                new Claim(ClaimTypes.Role, "Manager")
            }, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
