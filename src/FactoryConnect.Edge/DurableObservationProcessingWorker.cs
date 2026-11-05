using FactoryConnect.Core;
using FactoryConnect.Core.Machines;

namespace FactoryConnect.Edge;

public sealed class DurableObservationProcessingWorker(
    DurableObservationProcessingPipelineSet pipelines,
    ILogger<DurableObservationProcessingWorker> logger)
    : BackgroundService
{
    private static readonly Action<ILogger, Exception?> ProcessingStarting =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(1, nameof(ProcessingStarting)),
            "FactoryConnect durable observation processing starting.");

    private static readonly Action<ILogger, string, Exception?> ProcessingHeld =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(2, nameof(ProcessingHeld)),
            "FactoryConnect activity chronology hold: {Diagnostic}");

    private void LogHold(MachineStateActivityChronologyHold hold) =>
        ProcessingHeld(logger, hold.ToDiagnosticString(), null);

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        ProcessingStarting(logger, null);
        pipelines.ChronologyHeld += LogHold;
        try
        {
            await pipelines.RunAsync(stoppingToken);
        }
        finally
        {
            pipelines.ChronologyHeld -= LogHold;
        }
    }
}
