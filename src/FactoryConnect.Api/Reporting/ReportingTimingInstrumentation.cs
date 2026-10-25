using System.Diagnostics;

namespace FactoryConnect.Api.Reporting;

/// <summary>Opt-in metadata-only timing logs for one correlated reporting request.</summary>
internal static class ReportingTimingInstrumentation
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
            logger.LogInformation("Reporting HTTP request received");
            context.Response.OnCompleted(() =>
            {
                logger.LogInformation("Reporting HTTP response completed RequestId={RequestId} TraceId={TraceId} Status={Status} ElapsedMs={ElapsedMs}",
                    context.TraceIdentifier, traceId, context.Response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                return Task.CompletedTask;
            });
            try { await next(context); }
            finally
            {
                logger.LogInformation("Reporting HTTP pipeline ended Status={Status} Aborted={Aborted} ElapsedMs={ElapsedMs}",
                    context.Response.StatusCode, context.RequestAborted.IsCancellationRequested, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        });

        void Write(Activity activity, string phase) => logger.LogInformation(
            "Reporting stage={Stage} Phase={Phase} TraceId={TraceId} SpanId={SpanId} ParentSpanId={ParentSpanId} ElapsedMs={ElapsedMs} Processor={Processor} Revision={Revision} InputCount={InputCount} Completed={Completed}",
            activity.OperationName, phase, activity.TraceId.ToString(), activity.SpanId.ToString(), activity.ParentSpanId.ToString(),
            activity.Duration.TotalMilliseconds, FindTag(activity, "processor.id"), FindTag(activity, "revision.position"),
            activity.GetTagItem("input.count"), activity.GetTagItem("stage.completed"));
    }

    private static object? FindTag(Activity? activity, string key)
    {
        for (var current = activity; current is not null; current = current.Parent)
            if (current.GetTagItem(key) is { } value) return value;
        return null;
    }
}
