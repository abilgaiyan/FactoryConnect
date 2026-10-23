using FactoryConnect.Core;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Edge;

public sealed class DurableObservationProcessingWorker(
    DurableObservationProcessingPipelineSet pipelines,
    ILogger<DurableObservationProcessingWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan SqlTimeoutRetryDelay = TimeSpan.FromSeconds(5);

    private static readonly Action<ILogger, Exception?> ProcessingStarting =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(1, nameof(ProcessingStarting)),
            "FactoryConnect durable observation processing starting.");

    private static readonly Action<ILogger, Exception?> ProcessingSqlTimeout =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(2, nameof(ProcessingSqlTimeout)),
            "FactoryConnect durable observation processing encountered a SQL command timeout; retrying from durable checkpoints.");

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        ProcessingStarting(logger, null);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await pipelines.RunAsync(stoppingToken);
                return;
            }
            catch (SqlException exception)
                when (exception.Number == -2 && !stoppingToken.IsCancellationRequested)
            {
                ProcessingSqlTimeout(logger, exception);
                try
                {
                    await Task.Delay(SqlTimeoutRetryDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }
}
