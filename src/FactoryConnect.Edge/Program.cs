using FactoryConnect.Edge;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<FactoryConnectWorker>();

await builder.Build().RunAsync();
