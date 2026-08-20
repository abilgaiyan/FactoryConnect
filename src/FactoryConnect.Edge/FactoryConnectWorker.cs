namespace FactoryConnect.Edge;

public sealed class FactoryConnectWorker(ILogger<FactoryConnectWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> EdgeStarting =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(1, nameof(EdgeStarting)),
            "FactoryConnect Edge starting.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        EdgeStarting(logger, null);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}
