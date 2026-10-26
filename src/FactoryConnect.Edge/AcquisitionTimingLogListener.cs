using System.Diagnostics;

namespace FactoryConnect.Edge;

/// <summary>Optional acquisition timing sink; records metadata, never observation/evidence content.</summary>
internal sealed partial class AcquisitionTimingLogListener(ILoggerFactory loggerFactory) : IHostedService, IDisposable
{
    private readonly ILogger _logger = loggerFactory.CreateLogger("FactoryConnect.Acquisition.Timing");
    private ActivityListener? _listener;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "FactoryConnect.Acquisition",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity => Write(activity, "started"),
            ActivityStopped = activity => Write(activity, "stopped")
        };
        ActivitySource.AddActivityListener(_listener);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose() { _listener?.Dispose(); _listener = null; }

    private void Write(Activity activity, string phase)
    {
        if (!_logger.IsEnabled(LogLevel.Information)) return;
        var traceId = activity.TraceId.ToString();
        var spanId = activity.SpanId.ToString();
        var parentId = activity.ParentSpanId.ToString();
        var machine = activity.GetTagItem("machine.id");
        var stream = activity.GetTagItem("stream.key");
        var publications = activity.GetTagItem("publication.count");
        var observations = activity.GetTagItem("observation.count");
        var commands = activity.GetTagItem("sql.command.count");
        var completed = activity.GetTagItem("stage.completed");
        Timing(_logger, activity.OperationName, phase, traceId, spanId, parentId, activity.Duration.TotalMilliseconds,
            machine, stream, publications, observations, commands, completed);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, SkipEnabledCheck = true,
        Message = "Acquisition Stage={Stage} Phase={Phase} TraceId={TraceId} SpanId={SpanId} ParentSpanId={ParentSpanId} ElapsedMs={ElapsedMs} Machine={Machine} Stream={Stream} Publications={Publications} Observations={Observations} SqlCommands={SqlCommands} Completed={Completed}")]
    private static partial void Timing(ILogger logger, string stage, string phase, string traceId, string spanId,
        string parentSpanId, double elapsedMs, object? machine, object? stream, object? publications,
        object? observations, object? sqlCommands, object? completed);
}
