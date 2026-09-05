using FactoryConnect.Edge;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddFactoryConnectEdgeApplication(builder.Configuration);
builder.Services.AddSingleton<IEdgeStartupCancellationRegistrationFactory,
    EdgeStartupCancellationRegistrationFactory>();

var host = builder.Build();

await EdgePersistenceStartup.RunAsync(
    host.Services,
    () => host.RunAsync());
