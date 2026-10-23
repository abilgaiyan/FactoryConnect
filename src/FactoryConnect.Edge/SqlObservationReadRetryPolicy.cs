using FactoryConnect.Core;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Edge;

public sealed class SqlObservationReadRetryPolicy(
    ILogger<DurableObservationProcessingWorker> logger) : IObservationReadRetryPolicy
{
    private static readonly Action<ILogger, Exception?> ProcessingSqlTimeout =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(2, nameof(ProcessingSqlTimeout)),
            "FactoryConnect raw observation read encountered a SQL command timeout; retrying the read before processing.");

    public bool ShouldRetry(Exception exception) =>
        exception is SqlException { Number: -2 };

    public Task DelayAsync(Exception exception, CancellationToken cancellationToken)
    {
        ProcessingSqlTimeout(logger, exception);
        return Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
    }
}
