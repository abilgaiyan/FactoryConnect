using FactoryConnect.Edge;

var builder = Host.CreateApplicationBuilder(args);
if (builder.Configuration.GetValue<bool>("AcquisitionDiagnostics:Enabled"))
    builder.Services.AddHostedService<AcquisitionTimingLogListener>();

builder.Services.AddFactoryConnectEdgeApplication(builder.Configuration);
builder.Services.AddSingleton<IEdgeStartupCancellationRegistrationFactory,
    EdgeStartupCancellationRegistrationFactory>();

var host = builder.Build();

await EdgePersistenceStartup.RunAsync(
    host.Services,
    () => host.RunAsync());
