using FactoryConnect.Core;

namespace FactoryConnect.Edge;

public sealed class DurableObservationProcessingWorker(
    DurableObservationProcessingPipeline pipeline,
    ILogger<DurableObservationProcessingWorker> logger)
    : BackgroundService
{
    private static readonly Action<ILogger, Exception?> ProcessingStarting =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(1, nameof(ProcessingStarting)),
            "FactoryConnect durable observation processing starting.");

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        ProcessingStarting(logger, null);
        await pipeline.RunAsync(stoppingToken);
    }
}
