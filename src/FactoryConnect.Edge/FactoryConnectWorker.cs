namespace FactoryConnect.Edge;

public sealed class FactoryConnectWorker(
    IMtConnectAcquisitionRuntimeFactory runtimeFactory,
    ILogger<FactoryConnectWorker> logger)
    : BackgroundService
{
    private static readonly Action<ILogger, Exception?> EdgeStarting =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(1, nameof(EdgeStarting)),
            "FactoryConnect Edge starting.");

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        EdgeStarting(logger, null);

        var runtime = await runtimeFactory.CreateAsync(
            stoppingToken);

        await runtime.RunAsync(stoppingToken);
    }
}
