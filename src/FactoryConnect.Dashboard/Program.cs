using FactoryConnect.Dashboard;
using Microsoft.Extensions.Options;

const string shiftReportingPath = "api/reporting/v1/operational-metrics/shifts/query";
const string productionDayReportingPath = "api/reporting/v1/operational-metrics/production-days/query";

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IValidateOptions<DashboardOptions>, DashboardOptionsValidator>();
builder.Services
    .AddOptions<DashboardOptions>()
    .Bind(builder.Configuration.GetSection(DashboardOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHttpClient(ReportingGateway.ClientName, client =>
    client.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddSingleton<ReportingGateway>();

var app = builder.Build();

app.UseStaticFiles();

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
        sources));
});

app.MapPost('/' + shiftReportingPath, (HttpContext context, ReportingGateway gateway) =>
    ForwardExactReportingRouteAsync(context, gateway, shiftReportingPath));

app.MapPost('/' + productionDayReportingPath, (HttpContext context, ReportingGateway gateway) =>
    ForwardExactReportingRouteAsync(context, gateway, productionDayReportingPath));

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
