using FactoryConnect.Dashboard;
using Microsoft.Extensions.Options;

string[] SupervisorEntryMethods = [HttpMethods.Get, HttpMethods.Post];

const string shiftReportingPath = "api/reporting/v1/operational-metrics/shifts/query";
const string productionDayReportingPath = "api/reporting/v1/operational-metrics/production-days/query";
const string productionDayShiftReportingPath = "api/reporting/v1/operational-metrics/production-day-shifts/query";
const string currentMachineStatePrefix = "/api/machines/v1/";
const string currentMachineStateSuffix = "/current-state";

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IValidateOptions<DashboardOptions>, DashboardOptionsValidator>();
builder.Services
    .AddOptions<DashboardOptions>()
    .Bind(builder.Configuration.GetSection(DashboardOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHttpClient(ReportingGateway.ClientName, client =>
    client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false });
builder.Services.AddSingleton<ReportingGateway>();

var app = builder.Build();

app.UseStaticFiles();

app.MapPost("/api/auth/v1/login", (HttpContext context, ReportingGateway gateway) =>
    gateway.ForwardAsync(context, "api/auth/v1/login", HttpMethod.Post));
app.MapGet("/api/auth/v1/users", (HttpContext context, ReportingGateway gateway) =>
    gateway.ForwardAsync(context, "api/auth/v1/users", HttpMethod.Get));
app.MapPost("/api/auth/v1/users", (HttpContext context, ReportingGateway gateway) =>
    gateway.ForwardAsync(context, "api/auth/v1/users", HttpMethod.Post));
app.MapPut("/api/auth/v1/users/{userId:guid}/access", (Guid userId, HttpContext context, ReportingGateway gateway) =>
    gateway.ForwardAsync(context, $"api/auth/v1/users/{userId:D}/access", HttpMethod.Put));
app.MapPut("/api/auth/v1/users/{userId:guid}/password", (Guid userId, HttpContext context, ReportingGateway gateway) =>
    gateway.ForwardAsync(context, $"api/auth/v1/users/{userId:D}/password", HttpMethod.Put));
app.MapGet("/api/auth/v1/me", (HttpContext context, ReportingGateway gateway) =>
    gateway.ForwardAsync(context, "api/auth/v1/me", HttpMethod.Get));
app.MapPost("/api/auth/v1/logout", (HttpContext context, ReportingGateway gateway) =>
    gateway.ForwardAsync(context, "api/auth/v1/logout", HttpMethod.Post));

app.MapGet("/api/commissioning/v1/snapshot", async (
    HttpContext context, IConfiguration configuration, ReportingGateway gateway) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (!CommissioningDashboardAccess.Allows(context, configuration))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    await gateway.ForwardAsync(context, "api/commissioning/v1/snapshot", HttpMethod.Get).ConfigureAwait(false);
});


// Exact opt-in CNC diagnostic preview route. Only local browser requests can
// reach the local API; do not expose an arbitrary supervisor proxy.
app.MapPost("/api/supervisor/cnc-shift-evidence-preview", async (
    HttpContext context, IConfiguration configuration, ReportingGateway gateway) =>
{
    if (!configuration.GetValue<bool>("CncShiftPreview:EnableGateway") ||
        context.Connection.RemoteIpAddress is not { } remote ||
        !System.Net.IPAddress.IsLoopback(remote) ||
        context.Request.Headers.ContainsKey("Forwarded") ||
        context.Request.Headers.Keys.Any(static key =>
            key.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await gateway.ForwardAsync(context,
        "api/supervisor/cnc-shift-evidence-preview", HttpMethod.Post)
        .ConfigureAwait(false);
});

// Local-only supervisor entry gateway. Never forward factory-network requests
// through the dashboard to the loopback-only commissioning API.
app.MapMethods("/api/supervisor/v1/machines/{machineId:guid}/entries",
    SupervisorEntryMethods, async (HttpContext context, IConfiguration configuration,
        ReportingGateway gateway, Guid machineId) =>
{
    if (!configuration.GetValue<bool>("SupervisorLocalEntry:EnableGateway") ||
        context.Connection.RemoteIpAddress is not { } remote ||
        !System.Net.IPAddress.IsLoopback(remote) ||
        context.Request.Headers.ContainsKey("Forwarded") ||
        context.Request.Headers.Keys.Any(static key =>
            key.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    var relativePath = $"api/supervisor/v1/machines/{machineId:D}/entries" +
        context.Request.QueryString.Value;
    await gateway.ForwardAsync(context, relativePath,
        HttpMethods.IsGet(context.Request.Method) ? HttpMethod.Get : HttpMethod.Post)
        .ConfigureAwait(false);
});

// Local-only supervisor shift-target history and revision gateway.
// Explicit opt-in is required even for reads; never expose commissioning routes on LAN.
app.MapMethods("/api/supervisor/v1/machines/{machineId:guid}/shift-targets",
    SupervisorEntryMethods, async (HttpContext context, IConfiguration configuration,
        ReportingGateway gateway, Guid machineId) =>
{
    if (!configuration.GetValue<bool>("SupervisorLocalShiftTarget:EnableGateway") ||
        context.Connection.RemoteIpAddress is not { } remote ||
        !System.Net.IPAddress.IsLoopback(remote) ||
        context.Request.Headers.ContainsKey("Forwarded") ||
        context.Request.Headers.Keys.Any(static key =>
            key.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    var relativePath = $"api/supervisor/v1/machines/{machineId:D}/shift-targets" +
        context.Request.QueryString.Value;
    await gateway.ForwardAsync(context, relativePath,
        HttpMethods.IsGet(context.Request.Method) ? HttpMethod.Get : HttpMethod.Post)
        .ConfigureAwait(false);
});

// Explicit local ideal cycle gateway.
app.MapMethods("/api/supervisor/v1/machines/{machineId:guid}/ideal-cycles",
    SupervisorEntryMethods, async (HttpContext context, IConfiguration configuration,
        ReportingGateway gateway, Guid machineId) =>
{
    if (!configuration.GetValue<bool>("SupervisorLocalShiftIdealCycle:EnableGateway") ||
        context.Connection.RemoteIpAddress is not { } remote ||
        !System.Net.IPAddress.IsLoopback(remote) ||
        context.Request.Headers.ContainsKey("Forwarded") ||
        context.Request.Headers.Keys.Any(static key =>
            key.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    var relativePath = $"api/supervisor/v1/machines/{machineId:D}/ideal-cycles" +
        context.Request.QueryString.Value;
    await gateway.ForwardAsync(context, relativePath,
        HttpMethods.IsGet(context.Request.Method) ? HttpMethod.Get : HttpMethod.Post)
        .ConfigureAwait(false);
});

// Explicit supervisor review/revision routes. Never proxy an arbitrary API path.
// The gateway opt-in and the API's commissioning/write checks are independent.
string[] supervisorReviewReadPaths =
[
    "/api/supervisor/v1/machines/{machineId:guid}/accepted-shift-production",
    "/api/supervisor/v1/machines/{machineId:guid}/quantity-source-cut",
    "/api/supervisor/v1/machines/{machineId:guid}/production-entries/{entryId:guid}/qualification-readiness",
    "/api/supervisor/v1/machines/{machineId:guid}/reviews",
    "/api/supervisor/v1/machines/{machineId:guid}/revision-reviews",
    "/api/supervisor/v1/machines/{machineId:guid}/downtime-reviews",
    "/api/supervisor/v1/machines/{machineId:guid}/downtime-explanations",
    "/api/supervisor/v1/machines/{machineId:guid}/entries/{entryId:guid}/revisions"
];
foreach (var route in supervisorReviewReadPaths)
{
    app.MapGet(route, async (HttpContext context, IConfiguration configuration,
        ReportingGateway gateway) =>
    {
        if (!AllowsSupervisorLocalGateway(context, configuration))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        await gateway.ForwardAsync(context,
            context.Request.Path.Value!.TrimStart('/') + context.Request.QueryString.Value,
            HttpMethod.Get).ConfigureAwait(false);
    });
}

string[] supervisorReviewWritePaths =
[
    "/api/supervisor/v1/machines/{machineId:guid}/entries/{entryId:guid}/review",
    "/api/supervisor/v1/machines/{machineId:guid}/entries/{entryId:guid}/revisions",
    "/api/supervisor/v1/machines/{machineId:guid}/revisions/{revisionId:guid}/review",
    "/api/supervisor/v1/machines/{machineId:guid}/downtime-explanations/{explanationId:guid}/review",
    "/api/supervisor/v1/machines/{machineId:guid}/downtime-explanations"
];
foreach (var route in supervisorReviewWritePaths)
{
    app.MapPost(route, async (HttpContext context, IConfiguration configuration,
        ReportingGateway gateway) =>
    {
        if (!AllowsSupervisorLocalGateway(context, configuration))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        await gateway.ForwardAsync(context,
            context.Request.Path.Value!.TrimStart('/') + context.Request.QueryString.Value,
            HttpMethod.Post).ConfigureAwait(false);
    });
}

static bool AllowsSupervisorLocalGateway(HttpContext context, IConfiguration configuration) =>
    configuration.GetValue<bool>("SupervisorLocalEntry:EnableGateway") &&
    context.Connection.RemoteIpAddress is { } remote &&
    System.Net.IPAddress.IsLoopback(remote) &&
    !context.Request.Headers.ContainsKey("Forwarded") &&
    !context.Request.Headers.Keys.Any(static key =>
        key.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase));

// Read only the two explicit downtime reason collections.
foreach (var reasonPath in new[] { "/api/supervisor/v1/downtime-reasons", "/api/supervisor/v1/downtime-reasons/all" })
{
    app.MapGet(reasonPath, async (HttpContext context, IConfiguration configuration, ReportingGateway gateway) =>
    {
        if (!configuration.GetValue<bool>("SupervisorLocalDowntimeReasons:EnableGateway") ||
            context.Connection.RemoteIpAddress is not { } remote ||
            !System.Net.IPAddress.IsLoopback(remote) ||
            context.Request.Headers.ContainsKey("Forwarded") ||
            context.Request.Headers.Keys.Any(static key =>
                key.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (!string.Equals(context.Request.Path.Value, reasonPath, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await gateway.ForwardAsync(context, reasonPath.TrimStart('/'), HttpMethod.Get)
            .ConfigureAwait(false);
    });
}

// Exact local-only downtime reason routes; never expose arbitrary supervisor API paths.
app.MapMethods("/api/supervisor/v1/downtime-reasons/{reasonCode}",
    [HttpMethods.Put], async (HttpContext context, IConfiguration configuration,
        ReportingGateway gateway, string reasonCode) =>
{
    if (!configuration.GetValue<bool>("SupervisorLocalDowntimeReasons:EnableGateway") ||
        context.Connection.RemoteIpAddress is not { } remote ||
        !System.Net.IPAddress.IsLoopback(remote) ||
        context.Request.Headers.ContainsKey("Forwarded") ||
        context.Request.Headers.Keys.Any(static key =>
            key.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    if (string.IsNullOrWhiteSpace(reasonCode) ||
        !string.Equals(context.Request.Path.Value,
            "/api/supervisor/v1/downtime-reasons/" + reasonCode, StringComparison.Ordinal))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    await gateway.ForwardAsync(context,
        "api/supervisor/v1/downtime-reasons/" + Uri.EscapeDataString(reasonCode),
        HttpMethod.Put).ConfigureAwait(false);
});

app.MapGet("/health/live", () => Results.Ok(new
{
    status = "ok",
    service = "FactoryConnect.Dashboard"
}));

app.MapGet("/health/ready", (IWebHostEnvironment environment) =>
{
    var index = environment.WebRootFileProvider.GetFileInfo("index.html");
    return index.Exists
        ? Results.Ok(new { status = "ready", service = "FactoryConnect.Dashboard" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});

app.MapGet("/dashboard/config", (IOptions<DashboardOptions> options) =>
{
    var dashboard = options.Value;
    var sources = dashboard.Sources
        .OrderBy(static source => source.DisplayOrder)
        .ThenBy(static source => source.GroupName, StringComparer.Ordinal)
        .ThenBy(static source => source.DisplayName, StringComparer.Ordinal)
        .ThenBy(static source => source.MachineId)
        .ThenBy(static source => source.ProcessorId, StringComparer.Ordinal)
        .Select(static source => new DashboardRuntimeSource(
            source.MachineId,
            source.ProcessorId,
            source.SiteId,
            source.ProductionLineId,
            source.DisplayName,
            source.GroupName,
            source.DisplayOrder))
        .ToArray();

    return Results.Ok(new DashboardRuntimeConfiguration(
        "/",
        checked((int)dashboard.RequestTimeout.TotalMilliseconds),
        sources,
        1,
        DashboardMachineProfileAdapter.Project(dashboard)));
});

app.MapPost('/' + shiftReportingPath, (HttpContext context, ReportingGateway gateway) =>
    ForwardExactReportingRouteAsync(context, gateway, shiftReportingPath));

app.MapPost('/' + productionDayReportingPath, (HttpContext context, ReportingGateway gateway) =>
    ForwardExactReportingRouteAsync(context, gateway, productionDayReportingPath));

app.MapPost('/' + productionDayShiftReportingPath, (HttpContext context, ReportingGateway gateway) =>
    ForwardExactReportingRouteAsync(context, gateway, productionDayShiftReportingPath));

app.MapGet("/api/machines/v1/{machineId}/current-state", async (
    HttpContext context,
    string machineId,
    ReportingGateway gateway) =>
{
    var expectedPath = currentMachineStatePrefix + machineId + currentMachineStateSuffix;
    if (!string.Equals(context.Request.Path.Value, expectedPath, StringComparison.Ordinal))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    await gateway
        .ForwardAsync(context, expectedPath.TrimStart('/'), HttpMethod.Get)
        .ConfigureAwait(false);
});

app.MapPost("/api/machines/v1/{machineId}/activity/query", async (
    HttpContext context, string machineId, ReportingGateway gateway) =>
{
    var expectedPath = "/api/machines/v1/" + machineId + "/activity/query";
    if (!string.Equals(context.Request.Path.Value, expectedPath, StringComparison.Ordinal))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    await gateway.ForwardAsync(context, expectedPath.TrimStart('/')).ConfigureAwait(false);
});

app.MapPost("/api/machines/v1/{machineId}/counter/query", async (
    HttpContext context, string machineId, ReportingGateway gateway) =>
{
    var expectedPath = "/api/machines/v1/" + machineId + "/counter/query";
    if (!string.Equals(context.Request.Path.Value, expectedPath, StringComparison.Ordinal))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    await gateway.ForwardAsync(context, expectedPath.TrimStart('/')).ConfigureAwait(false);
});

app.Map("{*path:nonfile}", (HttpContext context, IWebHostEnvironment environment) =>
{
    var path = context.Request.Path;
    if (IsReservedPath(path))
    {
        return Results.NotFound();
    }

    if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
    {
        return Results.NotFound();
    }

    var index = environment.WebRootFileProvider.GetFileInfo("index.html");
    return index.Exists
        ? Results.File(index.PhysicalPath!, "text/html")
        : Results.NotFound();
});

app.Run();

static async Task ForwardExactReportingRouteAsync(
    HttpContext context,
    ReportingGateway gateway,
    string relativePath)
{
    var expectedPath = '/' + relativePath;
    if (!string.Equals(context.Request.Path.Value, expectedPath, StringComparison.Ordinal))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    await gateway.ForwardAsync(context, relativePath).ConfigureAwait(false);
}

static bool IsReservedPath(PathString path) =>
    path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase) ||
    path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) ||
    path.StartsWithSegments("/dashboard", StringComparison.OrdinalIgnoreCase) ||
    path.StartsWithSegments("/config", StringComparison.OrdinalIgnoreCase) ||
    path.StartsWithSegments("/configuration", StringComparison.OrdinalIgnoreCase);

public partial class Program;

