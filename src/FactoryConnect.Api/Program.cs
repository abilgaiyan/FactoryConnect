using FactoryConnect.Api.Commissioning;
using FactoryConnect.Abstractions;
using FactoryConnect.Api;
using FactoryConnect.Api.Machines;
using FactoryConnect.Api.Reporting;
using FactoryConnect.Core.Machines;
using FactoryConnect.Edge;
using FactoryConnect.Infrastructure;
using FactoryConnect.Persistence;

if (args.Length > 0 && args[0] == "--create-admin")
{
    if (args.Length != 2)
        throw new ArgumentException("Usage: --create-admin <username>");
    var configuration = WebApplication.CreateBuilder(Array.Empty<string>()).Configuration;
    var connectionString = LocalUsers.ConnectionString(configuration)
        ?? throw new InvalidOperationException("SQL connection configuration is required.");
    var password = Environment.GetEnvironmentVariable("FC_INITIAL_ADMIN_PASSWORD")
        ?? throw new InvalidOperationException("FC_INITIAL_ADMIN_PASSWORD is required.");
    await new LocalUsers(connectionString).CreateAdminAsync(args[1], password, CancellationToken.None);
    Console.WriteLine("Initial administrator created.");
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddLocalAuthentication();

builder.Services.AddOpenApi(options =>
{
    options.AddSchemaTransformer<OperationalMetricStatusOpenApiTransformer>();
    options.AddSchemaTransformer<CurrentMachineStateOpenApiTransformer>();
});
builder.Services.AddOpenApi("activity-v1");
builder.Services.AddOpenApi("counter-v1");
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
builder.Services.AddSingleton<IProductionContextActivityReader>(provider =>
    provider.GetRequiredService<PersistenceProviderServices>().ProductionContextActivityReader
    ?? throw new InvalidOperationException("Selected provider has no machine activity reader."));

builder.Services.AddSingleton<IDurableObservationReader>(provider =>
    provider.GetRequiredService<PersistenceProviderServices>().ObservationIngestionStore as IDurableObservationReader
    ?? throw new InvalidOperationException("Selected provider has no raw observation reader."));

var currentStateInventory =
    MtConnectMachineInventory.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(
    CanonicalCurrentStateContinuityPolicies.Preserve);
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddFactoryConnectCurrentMachineState(
    builder.Configuration,
    currentStateInventory);

builder.Services.AddSingleton<CommissioningSnapshotReader>();

var app = builder.Build();
app.MapCommissioning();

app.UseReportingTimings();
app.UseExceptionHandler();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapLocalAuthentication();

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "FactoryConnect.Api"
}));

app.MapOpenApi();
app.MapOperationalMetricReportingEndpoints();
app.MapCurrentMachineStateEndpoints();
app.MapMachineActivityReports();
app.MapMachineCounterReports();
app.MapSupervisorProductionEntryEndpoints();
app.MapSupervisorShiftTargetEndpoints();
app.MapCncShiftEvidencePreviewEndpoints();
app.MapMachineEvidenceProfileEndpoints();

await ApiPersistenceStartup.RunAsync(
    app.Services,
    () => app.RunAsync());

public partial class Program;

