using FactoryConnect.Core;

namespace FactoryConnect.Edge;

public sealed class MetricAggregationProcessingWorker(
    MetricAggregationProcessingRuntimeSet runtimes,
    ILogger<MetricAggregationProcessingWorker> logger)
    : BackgroundService
{
    private static readonly Action<ILogger, Exception?> AggregationStarting =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(1, nameof(AggregationStarting)),
            "FactoryConnect metric aggregation processing starting.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AggregationStarting(logger, null);
        await runtimes.RunAsync(stoppingToken);
    }
}
