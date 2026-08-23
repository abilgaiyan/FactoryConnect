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

var retrySection = section.GetRequiredSection("Retry");

var maxAttempts = retrySection["MaxAttempts"]
    ?? throw new InvalidOperationException(
        "MTConnect:Retry:MaxAttempts is required.");

var initialDelay = retrySection["InitialDelay"]
    ?? throw new InvalidOperationException(
        "MTConnect:Retry:InitialDelay is required.");

var maximumDelay = retrySection["MaximumDelay"]
    ?? throw new InvalidOperationException(
        "MTConnect:Retry:MaximumDelay is required.");

var jitterRatio = retrySection["JitterRatio"]
    ?? throw new InvalidOperationException(
        "MTConnect:Retry:JitterRatio is required.");

var options = new MtConnectAcquisitionOptions(
    new MtConnectEndpoint(new Uri(baseUri, UriKind.Absolute)),
    new MachineId(Guid.Parse(machineId)),
    deviceKey,
    ulong.Parse(fromSequence, CultureInfo.InvariantCulture),
    TimeSpan.Parse(pollingInterval, CultureInfo.InvariantCulture));

var retryOptions = new MtConnectRetryOptions(
    int.Parse(maxAttempts, CultureInfo.InvariantCulture),
    TimeSpan.Parse(initialDelay, CultureInfo.InvariantCulture),
    TimeSpan.Parse(maximumDelay, CultureInfo.InvariantCulture),
    double.Parse(jitterRatio, CultureInfo.InvariantCulture));

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(retryOptions);
builder.Services.AddSingleton<HttpClient>();
builder.Services.AddSingleton<MtConnectSampleClient>();
builder.Services.AddSingleton(
    services => new MtConnectAcquisitionSession(
        services.GetRequiredService<MtConnectSampleClient>(),
        options.FromSequence));
builder.Services.AddSingleton<
    IMtConnectRetryDelay,
    SystemMtConnectRetryDelay>();
builder.Services.AddSingleton<
    IMtConnectJitterSource,
    SystemMtConnectJitterSource>();
builder.Services.AddSingleton<MtConnectTransientRetryPolicy>();
builder.Services.AddSingleton<
    IMtConnectObservationSink,
    LoggingMtConnectObservationSink>();
builder.Services.AddSingleton<IMtConnectAcquisitionRuntime>(
    services => new MtConnectAcquisitionRuntime(
        services.GetRequiredService<MtConnectAcquisitionSession>(),
        options.Endpoint,
        options.MachineId,
        options.DeviceKey,
        services.GetRequiredService<MtConnectTransientRetryPolicy>(),
        services.GetRequiredService<IMtConnectObservationSink>(),
        options.PollingInterval));
builder.Services.AddHostedService<FactoryConnectWorker>();

await builder.Build().RunAsync();
