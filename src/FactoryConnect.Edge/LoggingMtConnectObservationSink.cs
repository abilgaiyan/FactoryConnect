using FactoryConnect.Abstractions;
using FactoryConnect.Protocols.MTConnect;

namespace FactoryConnect.Edge;

public sealed class LoggingMtConnectObservationSink(
    ILogger<LoggingMtConnectObservationSink> logger)
    : IMtConnectObservationSink
{
    private static readonly Action<ILogger, ulong, ulong, int, Exception?>
        BatchAcquired =
            LoggerMessage.Define<ulong, ulong, int>(
                LogLevel.Information,
                new EventId(2, nameof(BatchAcquired)),
                "Acquired MTConnect batch for instance {InstanceId}; " +
                "next sequence {NextSequence}; observations {ObservationCount}.");

    public ValueTask WriteAsync(
        MtConnectSampleResult result,
        ObservationCheckpoint? expectedCheckpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();

        BatchAcquired(
            logger,
            result.InstanceId,
            result.NextSequence,
            result.Observations.Count,
            null);

        return ValueTask.CompletedTask;
    }
}
