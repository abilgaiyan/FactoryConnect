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

    private static readonly Action<ILogger, string, Exception?> AggregationFailed =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(2, nameof(AggregationFailed)),
            "Metric aggregation processor '{ProcessorId}' failed; its machine loop will retry independently.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AggregationStarting(logger, null);

        var loops = runtimes.Runtimes
            .Select(runtime => RunRuntimeAsync(runtime, stoppingToken))
            .ToArray();

        await Task.WhenAll(loops);
    }

    private async Task RunRuntimeAsync(
        MetricAggregationProcessingRuntime runtime,
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await runtime.RunCycleAsync(stoppingToken);
                if (processed > 0)
                {
                    continue;
                }
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                AggregationFailed(logger, runtime.ProcessorId.Value, exception);
            }

            try
            {
                await Task.Delay(runtimes.PollingInterval, stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
