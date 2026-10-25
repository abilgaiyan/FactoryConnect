using System.Diagnostics;

namespace FactoryConnect.Api.Reporting;

/// <summary>Opt-in metadata-only timing logs for one correlated reporting request.</summary>
internal static partial class ReportingTimingInstrumentation
{
    internal static void UseReportingTimings(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("ReportingDiagnostics:Enabled")) return;
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("FactoryConnect.Reporting.Timing");
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "FactoryConnect.Reporting",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity => Write(activity, "started"),
            ActivityStopped = activity => Write(activity, "stopped")
        };
        ActivitySource.AddActivityListener(listener);
        app.Lifetime.ApplicationStopped.Register(listener.Dispose);
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/api/reporting"))
            {
                await next(context);
                return;
            }
            var started = Stopwatch.GetTimestamp();
            var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
            using var scope = logger.BeginScope(new Dictionary<string, object?>
            {
                ["RequestId"] = context.TraceIdentifier, ["TraceId"] = traceId
            });
            RequestReceived(logger);
            context.Response.OnCompleted(() =>
            {
                if (logger.IsEnabled(LogLevel.Information))
                    ResponseCompleted(logger, context.TraceIdentifier, traceId, context.Response.StatusCode,
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                return Task.CompletedTask;
            });
            try { await next(context); }
            finally
            {
                if (logger.IsEnabled(LogLevel.Information))
                    PipelineEnded(logger, context.Response.StatusCode, context.RequestAborted.IsCancellationRequested,
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        });

        void Write(Activity activity, string phase)
        {
            if (!logger.IsEnabled(LogLevel.Information)) return;
            StageTiming(logger, activity.OperationName, phase, activity.TraceId.ToString(),
                activity.SpanId.ToString(), activity.ParentSpanId.ToString(), activity.Duration.TotalMilliseconds,
                FindTag(activity, "processor.id"), FindTag(activity, "revision.position"),
                activity.GetTagItem("input.count"), activity.GetTagItem("stage.completed"));
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Reporting HTTP request received")]
    private static partial void RequestReceived(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "Reporting HTTP response completed RequestId={RequestId} TraceId={TraceId} Status={Status} ElapsedMs={ElapsedMs}")]
    private static partial void ResponseCompleted(ILogger logger, string requestId, string traceId, int status, double elapsedMs);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information,
        Message = "Reporting HTTP pipeline ended Status={Status} Aborted={Aborted} ElapsedMs={ElapsedMs}")]
    private static partial void PipelineEnded(ILogger logger, int status, bool aborted, double elapsedMs);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information,
        Message = "Reporting stage={Stage} Phase={Phase} TraceId={TraceId} SpanId={SpanId} ParentSpanId={ParentSpanId} ElapsedMs={ElapsedMs} Processor={Processor} Revision={Revision} InputCount={InputCount} Completed={Completed}")]
    private static partial void StageTiming(ILogger logger, string stage, string phase, string traceId,
        string spanId, string parentSpanId, double elapsedMs, object? processor, object? revision,
        object? inputCount, object? completed);

    private static object? FindTag(Activity? activity, string key)
    {
        for (var current = activity; current is not null; current = current.Parent)
            if (current.GetTagItem(key) is { } value) return value;
        return null;
    }
}
