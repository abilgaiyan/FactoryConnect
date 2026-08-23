namespace FactoryConnect.Edge;

public sealed class FactoryConnectWorker(
    IMtConnectAcquisitionRuntime runtime,
    ILogger<FactoryConnectWorker> logger)
    : BackgroundService
{
    private static readonly Action<ILogger, Exception?> EdgeStarting =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(1, nameof(EdgeStarting)),
            "FactoryConnect Edge starting.");

    protected override Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        EdgeStarting(logger, null);

        return runtime.RunAsync(stoppingToken);
    }
}
