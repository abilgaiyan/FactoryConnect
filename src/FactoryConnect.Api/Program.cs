using FactoryConnect.Api;
using FactoryConnect.Api.Machines;
using FactoryConnect.Api.Reporting;
using FactoryConnect.Core.Machines;
using FactoryConnect.Edge;
using FactoryConnect.Infrastructure;
using FactoryConnect.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi(options =>
{
    options.AddSchemaTransformer<OperationalMetricStatusOpenApiTransformer>();
    options.AddSchemaTransformer<CurrentMachineStateOpenApiTransformer>();
});
builder.Services.AddSingleton<IApiStartupCancellationRegistrationFactory,
    ApiStartupCancellationRegistrationFactory>();
builder.Services.AddFactoryConnectPersistenceProviders(builder.Configuration);
builder.Services.AddFactoryConnectPersistence(
    builder.Configuration,
    PersistenceProviderCapabilities.OperationalMetricReportingQuery |
    PersistenceProviderCapabilities.OperationalMetricProjectionQuery |
    PersistenceProviderCapabilities.MachineShiftOccurrenceRoster |
    PersistenceProviderCapabilities.CurrentStateAuthorityReading);
builder.Services.AddFactoryConnectOperationalMetricReporting();

var currentStateInventory =
    MtConnectMachineInventory.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(
    CanonicalCurrentStateContinuityPolicies.Preserve);
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddFactoryConnectCurrentMachineState(
    builder.Configuration,
    currentStateInventory);

var app = builder.Build();

app.UseExceptionHandler();

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "FactoryConnect.Api"
}));

app.MapOpenApi();
app.MapOperationalMetricReportingEndpoints();
app.MapCurrentMachineStateEndpoints();

await ApiPersistenceStartup.RunAsync(
    app.Services,
    () => app.RunAsync());

public partial class Program;
