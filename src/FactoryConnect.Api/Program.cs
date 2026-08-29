using FactoryConnect.Api.Reporting;
using FactoryConnect.Infrastructure;
using FactoryConnect.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddFactoryConnectPersistenceProviders(builder.Configuration);
builder.Services.AddFactoryConnectPersistence(
    builder.Configuration,
    PersistenceProviderCapabilities.Reporting);
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
