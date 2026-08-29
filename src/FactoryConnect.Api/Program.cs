using FactoryConnect.Abstractions;
using FactoryConnect.Api.Reporting;
using FactoryConnect.Core;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddSingleton<InMemoryOperationalMetricProjectionStore>();
builder.Services.AddSingleton<IOperationalMetricReportingQueryProvider>(
    static provider => provider.GetRequiredService<InMemoryOperationalMetricProjectionStore>());
builder.Services.AddFactoryConnectOperationalMetricReporting();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "FactoryConnect.Api"
}));

app.MapOpenApi();
app.MapOperationalMetricReportingEndpoints();

app.Run();

public partial class Program;
