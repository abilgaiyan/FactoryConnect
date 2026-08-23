namespace FactoryConnect.Edge;

public sealed class LoggingMtConnectContinuityReporter(
    ILogger<LoggingMtConnectContinuityReporter> logger)
    : IMtConnectContinuityReporter
{
    private static readonly Action<
        ILogger,
        string,
        string,
        string,
        ulong,
        ulong,
        ulong,
        Exception?> ContinuityLost =
            LoggerMessage.Define<
                string,
                string,
                string,
                ulong,
                ulong,
                ulong>(
                LogLevel.Warning,
                new EventId(4, nameof(ContinuityLost)),
                "MTConnect continuity loss for machine {MachineId}: " +
                "{Reason}; previous instance {PreviousInstanceId}; " +
                "current instance {CurrentInstanceId}; " +
                "previous sequence {PreviousSequence}; " +
                "recovery sequence {RecoverySequence}.");

    public ValueTask ReportAsync(
        MtConnectContinuityLoss continuityLoss,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(continuityLoss);
        cancellationToken.ThrowIfCancellationRequested();

        ContinuityLost(
            logger,
            continuityLoss.MachineId.ToString(),
            continuityLoss.Reason.ToString(),
            continuityLoss.PreviousInstanceId?.ToString() ?? "unknown",
            continuityLoss.CurrentInstanceId,
            continuityLoss.PreviousSequence,
            continuityLoss.RecoverySequence,
            null);

        return ValueTask.CompletedTask;
    }
}
