using FactoryConnect.Api.Reporting;

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "FactoryConnect.Api"
}));

app.MapOperationalMetricReportingEndpoints();

app.Run();
