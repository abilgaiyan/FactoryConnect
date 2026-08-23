using System.Globalization;
using FactoryConnect.Abstractions;
using FactoryConnect.Edge;
using FactoryConnect.Protocols.MTConnect;

var builder = Host.CreateApplicationBuilder(args);
var section = builder.Configuration.GetRequiredSection("MTConnect");

var baseUri = section["BaseUri"]
    ?? throw new InvalidOperationException(
        "MTConnect:BaseUri is required.");

var machineId = section["MachineId"]
    ?? throw new InvalidOperationException(
        "MTConnect:MachineId is required.");

var deviceKey = section["DeviceKey"]
    ?? throw new InvalidOperationException(
        "MTConnect:DeviceKey is required.");

var fromSequence = section["FromSequence"]
    ?? throw new InvalidOperationException(
        "MTConnect:FromSequence is required.");

var pollingInterval = section["PollingInterval"]
    ?? throw new InvalidOperationException(
        "MTConnect:PollingInterval is required.");

var options = new MtConnectAcquisitionOptions(
    new MtConnectEndpoint(new Uri(baseUri, UriKind.Absolute)),
    new MachineId(Guid.Parse(machineId)),
    deviceKey,
    ulong.Parse(fromSequence, CultureInfo.InvariantCulture),
    TimeSpan.Parse(pollingInterval, CultureInfo.InvariantCulture));

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<HttpClient>();
builder.Services.AddSingleton<MtConnectSampleClient>();
builder.Services.AddSingleton(
    services => new MtConnectAcquisitionSession(
        services.GetRequiredService<MtConnectSampleClient>(),
        options.FromSequence));
builder.Services.AddSingleton<
    IMtConnectObservationSink,
    LoggingMtConnectObservationSink>();
builder.Services.AddSingleton<IMtConnectAcquisitionRuntime>(
    services => new MtConnectAcquisitionRuntime(
        services.GetRequiredService<MtConnectAcquisitionSession>(),
        options.Endpoint,
        options.MachineId,
        options.DeviceKey,
        services.GetRequiredService<IMtConnectObservationSink>(),
        options.PollingInterval));
builder.Services.AddHostedService<FactoryConnectWorker>();

await builder.Build().RunAsync();
