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
                    if (!Guid.TryParse(context.Principal?.FindFirstValue("fc.session-version"), out var version) || !Guid.TryParse(userId, out var id) || role is not ("Administrator" or "Supervisor" or "Manager") || string.IsNullOrWhiteSpace(connection))
                    {
                        context.RejectPrincipal();
                        return;
                    }
                    try
                    {
                        if (!await new LocalUsers(connection).IsActiveAsync(id, role, version, context.HttpContext.RequestAborted))
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
        services.AddAuthorization(options =>
        {
            options.AddPolicy("OperationalRead", policy => policy.RequireRole("Administrator", "Supervisor", "Manager"));
            options.AddPolicy("SupervisorWrite", policy => policy.RequireRole("Administrator", "Supervisor"));
            options.AddPolicy("AdministratorWrite", policy => policy.RequireRole("Administrator"));
        });
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
            (Guid Id, string Username, string Role, Guid SessionVersion)? user;
            try { user = await new LocalUsers(connection).VerifyAsync(request.Username, request.Password, http.RequestAborted); }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { throw; }
            catch (Exception) { return Results.Problem(statusCode: 503, title: "User database unavailable"); }
            if (user is null) return Results.Unauthorized();
            var claims = new[] {
                new Claim(ClaimTypes.NameIdentifier, user.Value.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Value.Username),
                new Claim(ClaimTypes.Role, user.Value.Role),
                new Claim("fc.session-version", user.Value.SessionVersion.ToString())
            };
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
            return Results.Ok(new { username = user.Value.Username, role = user.Value.Role });
        }).RequireRateLimiting("login").ExcludeFromDescription();
        app.MapGet("/api/auth/v1/users", async (HttpContext http, IConfiguration config) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var connection = LocalUsers.ConnectionString(config);
            if (string.IsNullOrWhiteSpace(connection)) return Results.Problem(statusCode: 503);
            try { return Results.Ok(await new LocalUsers(connection).ListAsync(http.RequestAborted)); }
            catch (Microsoft.Data.SqlClient.SqlException) { return Results.Problem(statusCode: 503); }
        }).RequireAuthorization("AdministratorWrite").ExcludeFromDescription();

        app.MapPost("/api/auth/v1/users", async (CreateUserRequest request, HttpContext http, IConfiguration config) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (http.Request.Headers["X-FC-Request"] != "create-user" ||
                request.Username is null || request.Password is null || request.Role is null)
                return Results.BadRequest();
            var connection = LocalUsers.ConnectionString(config);
            if (string.IsNullOrWhiteSpace(connection)) return Results.Problem(statusCode: 503);
            try
            {
                var id = await new LocalUsers(connection).CreateAsync(request.Username, request.Password, request.Role, http.RequestAborted);
                return Results.Ok(new { userId = id });
            }
            catch (ArgumentException) { return Results.BadRequest(new { reason = "InvalidUser" }); }
            catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 2601 or 2627)
            { return Results.Conflict(new { reason = "UsernameExists" }); }
            catch (Microsoft.Data.SqlClient.SqlException) { return Results.Problem(statusCode: 503); }
        }).RequireAuthorization("AdministratorWrite").ExcludeFromDescription();

        app.MapPut("/api/auth/v1/users/{userId:guid}/access", async (Guid userId, ChangeAccessRequest request, HttpContext http, IConfiguration config) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (http.Request.Headers["X-FC-Request"] != "change-access" ||
                request.Role is null || request.IsActive is null ||
                !Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId))
                return Results.BadRequest();
            var connection = LocalUsers.ConnectionString(config);
            if (string.IsNullOrWhiteSpace(connection)) return Results.Problem(statusCode: 503);
            try
            {
                return await new LocalUsers(connection).ChangeAccessAsync(actorId, userId, request.Role, request.IsActive.Value, http.RequestAborted)
                    ? Results.NoContent() : Results.NotFound();
            }
            catch (ArgumentException) { return Results.BadRequest(new { reason = "InvalidAccessChange" }); }
            catch (InvalidOperationException) { return Results.Conflict(new { reason = "AdministratorAccessChanged" }); }
            catch (Microsoft.Data.SqlClient.SqlException) { return Results.Problem(statusCode: 503); }
        }).RequireAuthorization("AdministratorWrite").ExcludeFromDescription();

        app.MapPut("/api/auth/v1/users/{userId:guid}/password", async (Guid userId, ResetPasswordRequest request, HttpContext http, IConfiguration config) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (http.Request.Headers["X-FC-Request"] != "reset-password" ||
                request.Password is null ||
                !Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId))
                return Results.BadRequest();
            var connection = LocalUsers.ConnectionString(config);
            if (string.IsNullOrWhiteSpace(connection)) return Results.Problem(statusCode: 503);
            try
            {
                if (!await new LocalUsers(connection).ResetPasswordAsync(actorId, userId, request.Password, http.RequestAborted))
                    return Results.NotFound();
                if (actorId == userId) await http.SignOutAsync();
                return Results.NoContent();
            }
            catch (ArgumentException) { return Results.BadRequest(new { reason = "InvalidPassword" }); }
            catch (InvalidOperationException) { return Results.Conflict(); }
            catch (Microsoft.Data.SqlClient.SqlException) { return Results.Problem(statusCode: 503); }
        }).RequireAuthorization("AdministratorWrite").ExcludeFromDescription();

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

    internal sealed record ResetPasswordRequest(string? Password);
    internal sealed record ChangeAccessRequest(string? Role, bool? IsActive);
    internal sealed record CreateUserRequest(string? Username, string? Password, string? Role);
    internal sealed record LoginRequest(string? Username, string? Password);
}
