using FactoryConnect.Dashboard;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IValidateOptions<DashboardOptions>, DashboardOptionsValidator>();
builder.Services
    .AddOptions<DashboardOptions>()
    .Bind(builder.Configuration.GetSection(DashboardOptions.SectionName))
    .ValidateOnStart();

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

app.MapFallback((HttpContext context, IWebHostEnvironment environment) =>
{
    var path = context.Request.Path;
    if (IsReservedPath(path) || Path.HasExtension(path.Value))
    {
        return Results.NotFound();
    }

    var index = environment.WebRootFileProvider.GetFileInfo("index.html");
    return index.Exists
        ? Results.File(index.PhysicalPath!, "text/html")
        : Results.NotFound();
});

app.Run();

static bool IsReservedPath(PathString path) =>
    path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase) ||
    path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) ||
    path.StartsWithSegments("/config", StringComparison.OrdinalIgnoreCase) ||
    path.StartsWithSegments("/configuration", StringComparison.OrdinalIgnoreCase);

public partial class Program;
