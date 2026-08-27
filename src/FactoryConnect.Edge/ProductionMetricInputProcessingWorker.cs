using FactoryConnect.Core;

namespace FactoryConnect.Edge;

public sealed class ProductionMetricInputProcessingWorker(
    ProductionMetricInputRuntimeSet runtimes,
    ILogger<ProductionMetricInputProcessingWorker> logger)
    : BackgroundService
{
    private static readonly Action<ILogger, string, Exception?> ProducerFailed =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(1, nameof(ProducerFailed)),
            "Production metric-input processor '{ProcessorId}' failed; its loop will retry independently.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var activityLoops = runtimes.ActivityRuntimes
            .Select(runtime => RunActivityAsync(runtime, stoppingToken));
        var quantityLoops = runtimes.QuantityRuntimes
            .Select(runtime => RunQuantityAsync(runtime, stoppingToken));

        await Task.WhenAll(activityLoops.Concat(quantityLoops));
    }

    private async Task RunActivityAsync(
        ProductionContextProcessingRuntime runtime,
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await runtime.RunCycleAsync(stoppingToken) > 0)
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
                ProducerFailed(logger, runtime.ProcessorId.Value, exception);
            }

            if (!await DelayAsync(stoppingToken))
            {
                break;
            }
        }
    }

    private async Task RunQuantityAsync(
        ProductionQuantityFactProcessingRuntime runtime,
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await runtime.RunCycleAsync(stoppingToken) > 0)
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
                ProducerFailed(logger, runtime.ProcessorId.Value, exception);
            }

            if (!await DelayAsync(stoppingToken))
            {
                break;
            }
        }
    }

    private async Task<bool> DelayAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(runtimes.PollingInterval, stoppingToken);
            return true;
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
