using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>One bounded record per load, independent of detail Activity admission. Elapsed time, never CPU.</summary>
internal sealed class HistoricalLoadDiagnostics(Action<string> sink, Action<string> failureSink,
    string trace, string attempt, string processor, string revision) : IDisposable
{
    private readonly string _id = Guid.NewGuid().ToString("N");
    private readonly long _started = Stopwatch.GetTimestamp();
    private long _readCalls, _readCompleted, _readInterrupted, _returned, _added, _materializations;
    private double _readMs, _readMaxMs, _materializeMs, _materializeMaxMs, _cleanupMs;
    private string _outcome = "Completed";
    private string _cleanupOutcome = "NotStarted";
    private bool _enumerationCompleted;
    private int _disposed;

    internal const int SlowReadSampleLimit = 4;
    internal const double SlowReadThresholdMs = 100;
    private readonly List<string> _slowReads = [];
    private long _slowReadCount, _readMaxOrdinal;
    private DateTimeOffset _readStartUtc, _readMaxStartUtc, _readMaxEndUtc;
    private string _readMaxOutcome = "Unavailable";
    private string _sqlSessionId = "Unavailable";

    // Local provider metadata only: no extra SQL command or connection details.
    internal void CaptureSqlSession(SqlConnection connection)
    {
        try { _sqlSessionId = connection.ServerProcessId.ToString(CultureInfo.InvariantCulture); }
        catch { _sqlSessionId = "Unavailable"; }
    }
    internal long BeginRead()
    {
        _readCalls++;
        _readStartUtc = DateTimeOffset.UtcNow;
        return Stopwatch.GetTimestamp();
    }
    internal void EndRead(long started, bool? returned)
    {
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _readMs += elapsed;
        // Diagnostic bookkeeping must not replace an interrupted read's exception.
        try
        {
            var endedUtc = DateTimeOffset.UtcNow;
            var outcome = returned is null ? "Interrupted" : returned.Value ? "Row" : "EndOfResults";
            if (_readMaxOrdinal == 0 || elapsed > _readMaxMs)
            {
                _readMaxOrdinal = _readCalls;
                _readMaxStartUtc = _readStartUtc;
                _readMaxEndUtc = endedUtc;
                _readMaxOutcome = outcome;
            }
            if (elapsed >= SlowReadThresholdMs)
            {
                _slowReadCount++;
                if (_slowReads.Count < SlowReadSampleLimit)
                    _slowReads.Add(FormattableString.Invariant(
                        $"{_readCalls},{elapsed},{_readStartUtc:O},{endedUtc:O},{outcome}"));
            }
        }
        catch { /* Keep authoritative reader behavior, including cancellation. */ }
        _readMaxMs = Math.Max(_readMaxMs, elapsed);
        if (returned is null) _readInterrupted++;
        else
        {
            _readCompleted++;
            if (returned.Value) _returned++;
            else _enumerationCompleted = true;
        }
    }
    internal long BeginMaterialization() { _materializations++; return Stopwatch.GetTimestamp(); }
    internal void EndMaterialization(long started)
    {
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _materializeMs += elapsed; _materializeMaxMs = Math.Max(_materializeMaxMs, elapsed);
    }
    internal void RowAdded() => _added++;
    internal void Cleanup(long started, Exception? error, CancellationToken token)
    {
        _cleanupMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _cleanupOutcome = error is null ? "Completed" : Classify(error, token);
    }
    internal void Fail(Exception error, CancellationToken token) => _outcome = Classify(error, token);
    private static string Classify(Exception error, CancellationToken token) =>
        error is OperationCanceledException || (error is SqlException && token.IsCancellationRequested) ? "Cancelled" : "Failed";
    internal string Snapshot() => string.Join(";", new[]
    {
        "RecordType=HistoricalLoad", $"LoadId={_id}", $"TraceId={trace}", $"AttemptId={attempt}",
        $"AggregationProcessor={processor}", $"Checkpoint={revision}", $"Outcome={_outcome}",
        $"EnumerationCompleted={_enumerationCompleted}", $"ReadCalls={_readCalls}",
        $"ReadCompleted={_readCompleted}", $"ReadInterrupted={_readInterrupted}",
        $"RowsReturned={_returned}", $"RowsAdded={_added}", $"MaterializationAttempts={_materializations}",
        $"ReadElapsedMs={F(_readMs)}", $"ReadMaxMs={F(_readMaxMs)}",
        $"SqlSessionId={_sqlSessionId}", $"ReadMaxOrdinal={_readMaxOrdinal}",
        $"ReadMaxStartUtc={Utc(_readMaxStartUtc)}", $"ReadMaxEndUtc={Utc(_readMaxEndUtc)}",
        $"ReadMaxOutcome={_readMaxOutcome}", $"SlowReadThresholdMs={F(SlowReadThresholdMs)}",
        $"SlowReadCount={_slowReadCount}", $"SlowReadSampleLimit={SlowReadSampleLimit}",
        $"SlowReadSamples={string.Join("|", _slowReads)}",
        "SlowReadSampleFields=Ordinal,ElapsedMs,StartUtc,EndUtc,Outcome",
        "ServerWaitEvidence=NotCollected",
        $"MaterializeValidateAddElapsedMs={F(_materializeMs)}", $"MaterializeValidateAddMaxMs={F(_materializeMaxMs)}",
        $"CleanupElapsedMs={F(_cleanupMs)}", $"CleanupOutcome={_cleanupOutcome}",
        $"LoadElapsedMs={F(Stopwatch.GetElapsedTime(_started).TotalMilliseconds)}",
        "TimingSemantics=ElapsedNotCpu;SqlCoverage=Unknown"
    });
    private static string Utc(DateTimeOffset value) =>
        value == default ? "Unavailable" : value.ToString("O", CultureInfo.InvariantCulture);
    private static string F(double value) => value.ToString(CultureInfo.InvariantCulture);
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { sink(Snapshot()); }
        catch
        {
            try { failureSink($"RecordType=HistoricalLoad;LoadId={_id};TraceId={trace};AttemptId={attempt};FailureCategory=CollectionOrEmission;Delivery=Unverified"); }
            catch { /* Never replace the reporting outcome; delivery remains unverified. */ }
        }
    }
}
