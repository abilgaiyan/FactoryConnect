using FactoryConnect.Core;

namespace FactoryConnect.Edge;

public sealed class OperationalMetricProjectionProcessingWorker(
    OperationalMetricProjectionProcessingRuntimeSet runtimes,
    ILogger<OperationalMetricProjectionProcessingWorker> logger)
    : BackgroundService
{
    private static readonly Action<ILogger, Exception?> ProcessingStarting =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(1, nameof(ProcessingStarting)),
            "FactoryConnect operational metric projection processing starting.");

    private static readonly Action<ILogger, string, Exception?> ProcessingFailed =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(2, nameof(ProcessingFailed)),
            "Operational metric projection processor '{ProcessorId}' failed; its machine loop will retry independently.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ProcessingStarting(logger, null);

        var loops = runtimes.Runtimes
            .Select(runtime => RunRuntimeAsync(runtime, stoppingToken))
            .ToArray();

        await Task.WhenAll(loops);
    }

    private async Task RunRuntimeAsync(
        OperationalMetricProjectionProcessingRuntime runtime,
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
                ProcessingFailed(logger, runtime.ProcessorId.Value, exception);
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
