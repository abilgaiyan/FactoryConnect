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

    internal long BeginRead() { _readCalls++; return Stopwatch.GetTimestamp(); }
    internal void EndRead(long started, bool? returned)
    {
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _readMs += elapsed; _readMaxMs = Math.Max(_readMaxMs, elapsed);
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
        $"MaterializeValidateAddElapsedMs={F(_materializeMs)}", $"MaterializeValidateAddMaxMs={F(_materializeMaxMs)}",
        $"CleanupElapsedMs={F(_cleanupMs)}", $"CleanupOutcome={_cleanupOutcome}",
        $"LoadElapsedMs={F(Stopwatch.GetElapsedTime(_started).TotalMilliseconds)}",
        "TimingSemantics=ElapsedNotCpu;SqlCoverage=Unknown"
    });
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
