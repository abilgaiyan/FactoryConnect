using FactoryConnect.Api.Reporting;
using FactoryConnect.Infrastructure;
using FactoryConnect.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi(options =>
    options.AddSchemaTransformer<OperationalMetricStatusOpenApiTransformer>());
builder.Services.AddFactoryConnectPersistenceProviders(builder.Configuration);
builder.Services.AddFactoryConnectPersistence(
    builder.Configuration,
    PersistenceProviderCapabilities.OperationalMetricReportingQuery);
builder.Services.AddFactoryConnectOperationalMetricReporting();

var app = builder.Build();

app.UseExceptionHandler();

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "FactoryConnect.Api"
}));

app.MapOpenApi();
app.MapOperationalMetricReportingEndpoints();

app.Run();

public partial class Program;
