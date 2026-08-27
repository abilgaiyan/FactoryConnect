using FactoryConnect.Edge;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddFactoryConnectEdgeApplication(builder.Configuration);
await builder.Build().RunAsync();
