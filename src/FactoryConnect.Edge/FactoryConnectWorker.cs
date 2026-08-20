namespace FactoryConnect.Edge;

public sealed class FactoryConnectWorker(ILogger<FactoryConnectWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("FactoryConnect Edge starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}
