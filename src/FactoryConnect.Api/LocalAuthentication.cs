using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;

namespace FactoryConnect.Api;

internal static class LocalAuthentication
{
    internal static void AddLocalAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "fc1.session";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.SlidingExpiration = false;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.Events.OnValidatePrincipal = async context =>
                {
                    var userId = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                    var role = context.Principal?.FindFirstValue(ClaimTypes.Role);
                    var connection = LocalUsers.ConnectionString(context.HttpContext.RequestServices.GetRequiredService<IConfiguration>());
                    if (!Guid.TryParse(userId, out var id) || role is not ("Administrator" or "Supervisor" or "Manager") || string.IsNullOrWhiteSpace(connection))
                    {
                        context.RejectPrincipal();
                        return;
                    }
                    try
                    {
                        if (!await new LocalUsers(connection).IsActiveAsync(id, role, context.HttpContext.RequestAborted))
                            context.RejectPrincipal();
                    }
                    catch (Exception)
                    {
                        context.RejectPrincipal();
                    }
                };
                options.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
                options.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
            });
        services.AddAuthorization();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
    }

    internal static void MapLocalAuthentication(this WebApplication app)
    {
        app.MapPost("/api/auth/v1/login", async (LoginRequest request, HttpContext http, IConfiguration config) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (http.Request.Headers["X-FC-Request"] != "login") return Results.BadRequest();
            if (request.Username is null || request.Password is null ||
                request.Username.Length is < 3 or > 64 ||
                !request.Username.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') ||
                request.Password.Length is < 1 or > 256)
                return Results.Unauthorized();
            var connection = LocalUsers.ConnectionString(config);
            if (string.IsNullOrWhiteSpace(connection)) return Results.Problem(statusCode: 503, title: "User database not configured");
            (Guid Id, string Username, string Role)? user;
            try { user = await new LocalUsers(connection).VerifyAsync(request.Username, request.Password, http.RequestAborted); }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { throw; }
            catch (Exception) { return Results.Problem(statusCode: 503, title: "User database unavailable"); }
            if (user is null) return Results.Unauthorized();
            var claims = new[] {
                new Claim(ClaimTypes.NameIdentifier, user.Value.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Value.Username),
                new Claim(ClaimTypes.Role, user.Value.Role)
            };
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
            return Results.Ok(new { username = user.Value.Username, role = user.Value.Role });
        }).RequireRateLimiting("login").ExcludeFromDescription();
        app.MapGet("/api/auth/v1/me", (ClaimsPrincipal user) =>
            Results.Ok(new { username = user.Identity?.Name, role = user.FindFirstValue(ClaimTypes.Role) }))
            .RequireAuthorization().ExcludeFromDescription();
        app.MapPost("/api/auth/v1/logout", async (HttpContext http) =>
        {
            if (http.Request.Headers["X-FC-Request"] != "logout") return Results.BadRequest();
            await http.SignOutAsync();
            return Results.NoContent();
        }).RequireAuthorization().ExcludeFromDescription();
    }

    internal sealed record LoginRequest(string? Username, string? Password);
}
