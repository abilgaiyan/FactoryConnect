using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.Security;

public sealed record SecurityWebOptions(Uri Origin, string ConnectionString, string KeyDirectory, string CertificateThumbprint, byte[] RequestKey)
{
    public static SecurityWebOptions Read(IConfiguration config)
    {
        var s = config.GetRequiredSection("SupervisorSecurity");
        var origin = new Uri(s["CanonicalOrigin"] ?? throw new InvalidOperationException("Canonical origin required."), UriKind.Absolute);
        if (origin.Host.Contains('*', StringComparison.Ordinal) || origin.Scheme != "https" || origin.AbsolutePath != "/" || origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0)
            throw new InvalidOperationException("One literal HTTPS application origin is required.");
        var key = Convert.FromBase64String(s["RequestBindingKey"] ?? "");
        if (key.Length < 32) throw new InvalidOperationException("Persistent request binding key required.");
        return new(origin, s["ConnectionString"] ?? throw new InvalidOperationException("Security SQL authority required."),
            s["KeyDirectory"] ?? throw new InvalidOperationException("Persistent key directory required."),
            s["CertificateThumbprint"] ?? throw new InvalidOperationException("Data Protection certificate required."), key);
    }
}
public static class SecurityWeb
{
    private const string CookieName = "__Host-FactoryConnectSession";
    public static IServiceCollection AddSupervisorSecurity(this IServiceCollection services, IConfiguration config)
    {
        if (!config.GetValue<bool>("SupervisorSecurity:Enabled")) return services;
        var options = SecurityWebOptions.Read(config);
        if (string.IsNullOrWhiteSpace(options.ConnectionString) || !Path.IsPathFullyQualified(options.KeyDirectory)) throw new InvalidOperationException("Explicit SQL and absolute persistent key directory required.");
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine); store.Open(OpenFlags.ReadOnly);
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, options.CertificateThumbprint, validOnly: true);
        if (matches.Count != 1 || !matches[0].HasPrivateKey) throw new InvalidOperationException("One valid private-key certificate required.");
        services.AddSingleton(options);
        services.AddSingleton(new SqlSecurityAuthority(options.ConnectionString, options.RequestKey, TimeProvider.System));
        services.AddDataProtection().SetApplicationName("FactoryConnect.Supervisor.Security.v1")
            .PersistKeysToFileSystem(new DirectoryInfo(options.KeyDirectory)).ProtectKeysWithCertificate(matches[0]);
        services.AddAntiforgery(o => { o.HeaderName = "X-FactoryConnect-CSRF"; o.Cookie.Name = "__Host-FactoryConnectCSRF";
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always; o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Strict; });
        return services;
    }
    public static void MapSupervisorSecurity(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("SupervisorSecurity:Enabled")) return;
        var options = app.Services.GetRequiredService<SecurityWebOptions>();
        var group = app.MapGroup("/api/security/v1").WithGroupName("security-v1");
        group.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext; http.Response.Headers.CacheControl = "no-store";
            if (!http.Request.IsHttps || !string.Equals(http.Request.Host.Value, options.Origin.Authority, StringComparison.OrdinalIgnoreCase) ||
                http.Request.Headers.ContainsKey("Forwarded") || http.Request.Headers.Keys.Any(k => k.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)))
                return Results.StatusCode(403);
            if (http.Request.Method != "GET")
            {
                if (!string.Equals(http.Request.Headers.Origin.ToString(), options.Origin.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal)) return Results.StatusCode(403);
                try { await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http); }
                catch (AntiforgeryValidationException) { return Results.StatusCode(403); }
            }
            try { return await next(ctx); }
            catch (AuthorizationDeniedException) { return Results.StatusCode(403); }
            catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
            catch (ArgumentException) { return Results.BadRequest(new { reason = "InvalidSecurityRequest" }); }
            catch (SecurityOperationConflictException) { return Results.Conflict(new { reason = "SecurityOperationConflict" }); }
            catch (SecurityCommitOutcomeUnknownException) { return Results.Json(new { reason = "SecurityOutcomeUnknown", diagnosticId = http.TraceIdentifier }, statusCode: 503); }
            catch (InvalidOperationException) { return Results.Json(new { reason = "SecurityAuthorityUnavailable", diagnosticId = http.TraceIdentifier }, statusCode: 503); }
            catch (Microsoft.Data.SqlClient.SqlException) { return Results.Json(new { reason = "SecurityAuthorityUnavailable", diagnosticId = http.TraceIdentifier }, statusCode: 503); }
        });
        group.MapGet("/csrf", (HttpContext http, IAntiforgery anti) => Results.Ok(new { token = anti.GetAndStoreTokens(http).RequestToken }));
        group.MapPost("/login", async (HttpContext http, LoginRequest request, SqlSecurityAuthority authority, IDataProtectionProvider protection) =>
        {
            var result = await authority.LoginAsync(request.Username, request.Password, http.RequestAborted);
            if (result.Token is null) return Results.Json(new { reason = "AuthenticationUnavailable" }, statusCode: 401);
            http.Response.Cookies.Append(CookieName, protection.CreateProtector(CookieName).Protect(result.Token),
                new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = SecurityRules.AbsoluteLifetime });
            return Results.Ok(new { outcome = result.Outcome });
        });
        group.MapGet("/session", async (HttpContext http, SqlSecurityAuthority authority) =>
        {
            return Results.Ok(await authority.ReadSessionAsync(Secret(http), http.RequestAborted));
        });
        group.MapGet("/audit", async (HttpContext http, long? after, SqlSecurityAuthority authority) =>
            Results.Ok(await authority.ReadAuditAsync(Secret(http), after ?? 0, http.RequestAborted)));
        group.MapGet("/administration", async (HttpContext http, SqlSecurityAuthority authority) =>
            Results.Ok(await authority.ReadAdministrationAsync(Secret(http), http.RequestAborted)));
        group.MapPost("/commands", async (HttpContext http, SecurityCommand command, SqlSecurityAuthority authority) =>
            Results.Ok(await authority.ExecuteAsync(Secret(http), command, http.RequestAborted)));
        group.MapPost("/session-actions", async (HttpContext http, SessionRequest request, SqlSecurityAuthority authority) =>
        {
            var result = await authority.SessionActionAsync(Secret(http), request.Action, request.OperationId, http.RequestAborted);
            if (request.Action == "logout") http.Response.Cookies.Delete(CookieName, new CookieOptions { Secure = true, Path = "/" });
            return Results.Ok(result);
        });
    }
    private static string Secret(HttpContext http)
    {
        if (!http.Request.Cookies.TryGetValue(CookieName, out var cookie)) throw new UnauthorizedAccessException();
        try { return http.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector(CookieName).Unprotect(cookie); }
        catch (System.Security.Cryptography.CryptographicException) { throw new UnauthorizedAccessException(); }
    }
    public sealed record LoginRequest(string Username, string Password);
    public sealed record SessionRequest(Guid OperationId, string Action);
}
