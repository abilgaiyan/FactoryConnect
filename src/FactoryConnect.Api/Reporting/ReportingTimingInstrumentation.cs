using System.Diagnostics;
using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Api.Reporting;

/// <summary>Opt-in metadata-only timing logs for one correlated reporting request.</summary>
internal static partial class ReportingTimingInstrumentation
{
    internal static void UseReportingTimings(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("ReportingDiagnostics:Enabled")) return;
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("FactoryConnect.Reporting.Timing");
        var summaryLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("FactoryConnect.Reporting.Summary");
        var failureLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("FactoryConnect.Reporting.SummaryFailure");
        var includeIdentities = app.Configuration.GetValue<bool>("ReportingDiagnostics:IncludeCanonicalIdentities");
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "FactoryConnect.Reporting",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity =>
            {
                try
                {
                    if (includeIdentities) activity.SetTag("diagnostic.capture.identity", true);
                    Write(activity, "started");
                }
                catch { /* Diagnostics cannot change authoritative request execution. */ }
            },
            ActivityStopped = activity =>
            {
                try { Write(activity, "stopped"); }
                catch { /* Diagnostics cannot mask verification/cancellation failures. */ }
            }
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
            using var capture = new ReportingDiagnosticSession(
                record =>
                {
                    if (!summaryLogger.IsEnabled(LogLevel.Information)) throw new InvalidOperationException("Summary sink disabled.");
                    VerificationSummary(summaryLogger, record);
                },
                record =>
                {
                    if (!failureLogger.IsEnabled(LogLevel.Warning)) throw new InvalidOperationException("Fallback sink disabled.");
                    SummaryFailure(failureLogger, record);
                },
                record =>
                {
                    if (summaryLogger.IsEnabled(LogLevel.Information)) SummaryAcknowledgement(summaryLogger, record);
                },
                record =>
                {
                    if (!summaryLogger.IsEnabled(LogLevel.Information)) throw new InvalidOperationException("Historical load sink disabled.");
                    HistoricalLoadSummary(summaryLogger, record);
                });
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
                {
                    var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    ResponseCompleted(logger, context.TraceIdentifier, traceId, context.Response.StatusCode, elapsedMs);
                }
                return Task.CompletedTask;
            });
            try { await next(context); }
            finally
            {
                if (logger.IsEnabled(LogLevel.Information))
                {
                    var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    PipelineEnded(logger, context.Response.StatusCode, context.RequestAborted.IsCancellationRequested, elapsedMs);
                }
            }
        });

        void Write(Activity activity, string phase)
        {
            if (!logger.IsEnabled(LogLevel.Information)) return;
            var traceId = activity.TraceId.ToString();
            var spanId = activity.SpanId.ToString();
            var parentSpanId = activity.ParentSpanId.ToString();
            if (phase == "stopped")
            {
                var metadata = string.Join(";", activity.TagObjects
                    .Where(static tag => tag.Key.StartsWith("diagnostic.", StringComparison.Ordinal))
                    .Select(static tag => $"{tag.Key}={Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture)}"));
                if (includeIdentities && activity.GetTagItem("restricted.identity.canonical") is string canonical)
                    RestrictedIdentity(logger, traceId, spanId, canonical);
                var captureTruncated = FindTag(activity, "diagnostic.capture.truncated");
                DiagnosticMetadata(logger, activity.OperationName, traceId,
                    spanId, parentSpanId, metadata, captureTruncated);
            }
            var processor = FindTag(activity, "processor.id");
            var revision = FindTag(activity, "revision.position");
            var inputCount = activity.GetTagItem("input.count");
            var completed = activity.GetTagItem("stage.completed");
            var rootReconstructionInvocations = activity.GetTagItem("RootReconstructionInvocations");
            var referenceTimeAuthorityValidationExecutions = activity.GetTagItem("ReferenceTimeAuthorityValidationExecutions");
            var referenceTimeAuthorityCacheHits = activity.GetTagItem("ReferenceTimeAuthorityCacheHits");
            var referenceTimePeriodEvaluations = activity.GetTagItem("ReferenceTimePeriodEvaluations");
            var referenceTimeEvidenceContract = activity.GetTagItem("ReferenceTimeEvidenceContract");
            StageTiming(logger, activity.OperationName, phase, traceId, spanId, parentSpanId,
                activity.Duration.TotalMilliseconds, processor, revision, inputCount, completed,
                rootReconstructionInvocations, referenceTimeAuthorityValidationExecutions,
                referenceTimeAuthorityCacheHits, referenceTimePeriodEvaluations, referenceTimeEvidenceContract);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Reporting HTTP request received")]
    private static partial void RequestReceived(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, SkipEnabledCheck = true,
        Message = "Reporting HTTP response completed RequestId={RequestId} TraceId={TraceId} Status={Status} ElapsedMs={ElapsedMs}")]
    private static partial void ResponseCompleted(ILogger logger, string requestId, string traceId, int status, double elapsedMs);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, SkipEnabledCheck = true,
        Message = "Reporting HTTP pipeline ended Status={Status} Aborted={Aborted} ElapsedMs={ElapsedMs}")]
    private static partial void PipelineEnded(ILogger logger, int status, bool aborted, double elapsedMs);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, SkipEnabledCheck = true,
        Message = "Reporting stage={Stage} Phase={Phase} TraceId={TraceId} SpanId={SpanId} ParentSpanId={ParentSpanId} ElapsedMs={ElapsedMs} Processor={Processor} Revision={Revision} InputCount={InputCount} Completed={Completed} RootReconstructionInvocations={RootReconstructionInvocations} ReferenceTimeAuthorityValidationExecutions={ReferenceTimeAuthorityValidationExecutions} ReferenceTimeAuthorityCacheHits={ReferenceTimeAuthorityCacheHits} ReferenceTimePeriodEvaluations={ReferenceTimePeriodEvaluations} ReferenceTimeEvidenceContract={ReferenceTimeEvidenceContract}")]
    private static partial void StageTiming(ILogger logger, string stage, string phase, string traceId,
        string spanId, string parentSpanId, double elapsedMs, object? processor, object? revision,
        object? inputCount, object? completed, object? rootReconstructionInvocations,
        object? referenceTimeAuthorityValidationExecutions, object? referenceTimeAuthorityCacheHits,
        object? referenceTimePeriodEvaluations, object? referenceTimeEvidenceContract);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, SkipEnabledCheck = true,
        Message = "Reporting diagnostic Stage={Stage} TraceId={TraceId} SpanId={SpanId} ParentSpanId={ParentSpanId} Metadata={Metadata} CaptureTruncated={CaptureTruncated}")]
    private static partial void DiagnosticMetadata(ILogger logger, string stage, string traceId,
        string spanId, string parentSpanId, string metadata, object? captureTruncated);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, SkipEnabledCheck = true,
        Message = "Reporting restricted identity TraceId={TraceId} SpanId={SpanId} Canonical={Canonical}")]
    private static partial void RestrictedIdentity(ILogger logger, string traceId, string spanId, string canonical);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, SkipEnabledCheck = true,
        Message = "Reporting verification summary {Record}")]
    private static partial void VerificationSummary(ILogger logger, string record);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning, SkipEnabledCheck = true,
        Message = "Reporting summary failure {Record}")]
    private static partial void SummaryFailure(ILogger logger, string record);

    [LoggerMessage(EventId = 9, Level = LogLevel.Information, SkipEnabledCheck = true,
        Message = "Reporting summary acknowledgement {Record}")]
    private static partial void SummaryAcknowledgement(ILogger logger, string record);

    [LoggerMessage(EventId = 10, Level = LogLevel.Information, SkipEnabledCheck = true,
        Message = "Reporting historical load {Record}")]
    private static partial void HistoricalLoadSummary(ILogger logger, string record);

    private static object? FindTag(Activity? activity, string key)
    {
        for (var current = activity; current is not null; current = current.Parent)
            if (current.GetTagItem(key) is { } value) return value;
        return null;
    }
}
