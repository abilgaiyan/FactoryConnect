using System.Diagnostics;
using System.Globalization;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>Request-owned observational accounting, independent of Activity sampling and detail admission.</summary>
public sealed class ReportingDiagnosticSession : IDisposable
{
    private static readonly AsyncLocal<ReportingDiagnosticSession?> Ambient = new();
    private static readonly AsyncLocal<Attempt?> AmbientAttempt = new();
    private readonly ReportingDiagnosticSession? _previous;
    private readonly Action<string> _summarySink;
    private readonly Action<string> _failureSink;
    private readonly Action<string>? _acknowledgementSink;
    private readonly object _sync = new();
    private readonly Dictionary<string, int> _reservations = new(StringComparer.Ordinal);
    private bool _knownPopulation;
    private int _shared;
    private bool _disposed;
    private bool _collectionFailed;
    internal const int GlobalLimit = 4096;
    internal const int ProcessorLimit = 8;
    internal const int Reservation = 256;
    internal const int SharedLimit = GlobalLimit - ProcessorLimit * Reservation;
    internal static ReportingDiagnosticSession? Current => Ambient.Value;
    internal static Attempt? CurrentAttempt => AmbientAttempt.Value;

    public ReportingDiagnosticSession(Action<string> summarySink, Action<string> failureSink,
        Action<string>? acknowledgementSink = null)
    {
        ArgumentNullException.ThrowIfNull(summarySink);
        ArgumentNullException.ThrowIfNull(failureSink);
        _summarySink = summarySink;
        _failureSink = failureSink;
        _acknowledgementSink = acknowledgementSink;
        _previous = Ambient.Value;
        Ambient.Value = this;
    }

    /// <summary>Call after request validation, before verification; never changes execution ordering.</summary>
    public static void ReserveKnownProcessors(IEnumerable<string> processors)
    {
        if (processors is null) return;
        try
        {
            var session = Current;
            if (session is null) return;
            lock (session._sync)
            {
                if (session._reservations.Count != 0 || session._shared != 0) return;
                session._knownPopulation = true;
                foreach (var processor in processors)
                {
                    if (session._reservations.Count == ProcessorLimit) break;
                    session._reservations.TryAdd(processor, 0);
                }
            }
        }
        catch
        {
            if (Current is { } session) session._collectionFailed = true;
            CurrentAttempt?.Degrade();
        }
    }

    private bool ReserveProcessor(string processor)
    {
        lock (_sync)
        {
            if (_reservations.ContainsKey(processor)) return true;
            if (_knownPopulation || _reservations.Count == ProcessorLimit) return false;
            _reservations.Add(processor, 0);
            return true;
        }
    }

    internal bool Admit(string? processor)
    {
        lock (_sync)
        {
            if (_disposed) return false;
            if (processor is not null)
            {
                if (!_reservations.TryGetValue(processor, out var used))
                {
                    if (_knownPopulation || _reservations.Count == ProcessorLimit) return false;
                    _reservations.Add(processor, 0);
                }
                if (used < Reservation)
                {
                    _reservations[processor] = used + 1;
                    return true;
                }
            }
            if (_shared == SharedLimit) return false;
            _shared++;
            return true;
        }
    }

    internal static Attempt? BeginAttempt(string processor, int ordinal)
    {
        try { return Current is { } session ? new Attempt(session, processor, ordinal) : null; }
        catch
        {
            if (Current is { } session) session._collectionFailed = true;
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Ambient.Value = _previous;
    }

    internal sealed class Attempt : IDisposable
    {
        private readonly ReportingDiagnosticSession _owner;
        private readonly Attempt? _previous;
        private readonly object _sync = new();
        private readonly Dictionary<string, Counts> _counts = new(StringComparer.Ordinal);
        private readonly HashSet<MetricAggregationCheckpoint> _checkpoints = [];
        private readonly Dictionary<long, string> _active = [];
        private long _next;
        private readonly bool _reserved;
        private int? _referenceLoads;
        private int? _referenceHits;
        private int _finalized;
        private bool _collectorFailed;
        private bool _checkpointOverflow;
        private long _admitted;
        private long _suppressed;
        private readonly long _started = Stopwatch.GetTimestamp();
        internal string Processor { get; }
        internal string Id { get; } = Guid.NewGuid().ToString("N");
        internal int Ordinal { get; }
        internal string Outcome { get; private set; } = "Completed";
        internal string SummaryEmission { get; private set; } = "Unverified";
        internal string FallbackEmission { get; private set; } = "Unverified";
        internal bool EnumerationCompleted { get; private set; }
        internal long? RetainedTotal { get; private set; }
        internal string? Revision { get; private set; }
        internal long Lookups { get; private set; }
        internal long Hits { get; private set; }
        internal long Misses { get; private set; }
        internal long Loads { get; private set; }
        internal bool Retried { get; set; }
        internal string TraceId { get; } = Activity.Current?.TraceId.ToString() ?? "unavailable";
        internal Attempt(ReportingDiagnosticSession owner, string processor, int ordinal)
        {
            _owner = owner; Processor = processor; Ordinal = ordinal;
            _reserved = owner.ReserveProcessor(processor);
            _collectorFailed = owner._collectionFailed;
            _previous = AmbientAttempt.Value; AmbientAttempt.Value = this;
        }
        internal void Fail(Exception error, CancellationToken token = default)
        {
            // Match the enclosing stable-read cancellation translation, not token signalling alone.
            Outcome = error is OperationCanceledException || (error is Microsoft.Data.SqlClient.SqlException && token.IsCancellationRequested)
                ? "Cancelled" : "Failed";
        }
        internal void Degrade() { lock (_sync) _collectorFailed = true; }
        internal void Detail(bool admitted)
        {
            lock (_sync) { if (admitted) _admitted++; else _suppressed++; }
        }
        internal Operation? Start(string operation)
        {
            var category = operation switch
            {
                "containment.retained-row.verify" => "rows",
                "containment.result.operation" => "roots",
                "reporting.sql.execute" => "sql.execute",
                "reporting.sql.consume" => "sql.consume",
                "reporting.sql.cleanup" => "sql.cleanup",
                "reporting.gate.wait" => "gate",
                "containment.publication.operation" => "verification",
                _ => null,
            };
            if (category is null) return null;
            lock (_sync)
            {
                var count = Get(category); count.Started++; count.Active++;
                var id = ++_next;
                if (_active.Count < 64) _active.Add(id, category);
                return new Operation(this, category, id);
            }
        }
        private Counts Get(string category)
        {
            if (!_counts.TryGetValue(category, out var value)) _counts.Add(category, value = new());
            return value;
        }
        internal void Tag(string key, object? value)
        {
            lock (_sync)
            {
                if (key == "diagnostic.rows.total" && value is int total) RetainedTotal = total;
                if (key == "diagnostic.cache.lookups") Lookups++;
                if (key == "diagnostic.cache.hits") Hits++;
                if (key == "diagnostic.cache.misses") Misses++;
                if (key == "diagnostic.cache.loads") Loads++;
            }
        }
        internal void ReferenceStats(int loads, int hits)
        { lock (_sync) { _referenceLoads = loads; _referenceHits = hits; } }
        internal void Publication(string revision) { lock (_sync) Revision = revision; }
        internal void EnumerationFinished() { lock (_sync) EnumerationCompleted = true; }
        internal void Checkpoint(MetricAggregationCheckpoint cut)
        {
            lock (_sync)
            {
                if (_checkpoints.Contains(cut)) return;
                if (_checkpoints.Count == 4096) { _checkpointOverflow = true; return; }
                _checkpoints.Add(cut);
            }
        }
        internal string Snapshot()
        {
            lock (_sync)
            {
                var inconsistent = _counts.Values.Any(c => c.Started != c.Completed + c.Failed + c.Cancelled + c.Active || c.Active < 0);
                var active = _counts.Values.Sum(c => c.Active);
                var integrity = _collectorFailed || inconsistent || _checkpointOverflow ? "Unknown" : active != 0 ? "Degraded" : "Complete";
                var allowance = _reserved ? Reservation : 0;
                var rows = Get("rows");
                var fields = new List<string>
                {
                    $"TraceId={TraceId}", $"AttemptId={Id}", $"Processor={Processor}", $"Ordinal={Ordinal}",
                    $"Revision={Revision ?? "Unknown"}", $"Outcome={Outcome}", $"Retried={Retried}",
                    $"ElapsedMs={Stopwatch.GetElapsedTime(_started).TotalMilliseconds.ToString(CultureInfo.InvariantCulture)}",
                    $"RetainedTotal={RetainedTotal?.ToString(CultureInfo.InvariantCulture) ?? "Unknown"}",
                    $"RowsVisited={rows.Started}", $"RowsCompleted={rows.Completed}",
                    $"RowsInterrupted={rows.Failed + rows.Cancelled + rows.Active}",
                    $"RowsFailed={rows.Failed}", $"RowsCancelled={rows.Cancelled}",
                    $"EnumerationCompleted={EnumerationCompleted}", "SqlCoverage=Unknown",
                    $"SummaryIntegrity={integrity}", "SummaryDurability=Unknown",
                    $"DistinctCheckpoints={_checkpoints.Count}", $"CheckpointCountTruncated={_checkpointOverflow}",
                    $"ReferenceTimeLoads={_referenceLoads?.ToString(CultureInfo.InvariantCulture) ?? "Unknown"}",
                    $"ReferenceTimeHits={_referenceHits?.ToString(CultureInfo.InvariantCulture) ?? "Unknown"}",
                    $"CacheLookups={Lookups}", $"CacheHits={Hits}", $"CacheMisses={Misses}", $"CacheLoads={Loads}",
                    $"DetailReservedAllowance={allowance}", $"DetailAdmitted={_admitted}", $"DetailSuppressed={_suppressed}",
                    $"DetailCaptureTruncated={_suppressed != 0}", "DurationSemantics=OverlappingAccumulated",
                    $"UnresolvedDetailsTruncated={active > _active.Count}",
                    "Unresolved=" + string.Join(",", _active.Select(p => $"{p.Key}:{p.Value}")),
                };
                foreach (var category in new[] { "rows", "roots", "sql.execute", "sql.consume", "sql.cleanup", "gate", "verification" })
                {
                    var c = Get(category);
                    fields.Add($"{category}.Started={c.Started},Completed={c.Completed},Failed={c.Failed},Cancelled={c.Cancelled},Active={c.Active},ElapsedMs={c.ElapsedMs.ToString(CultureInfo.InvariantCulture)}");
                }
                return string.Join(";", fields);
            }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _finalized, 1) != 0) return;
            var submitted = false;
            try
            {
                string record;
                try { record = Snapshot(); }
                catch { Degrade(); throw; }
                submitted = true;
                _owner._summarySink(record);
                SummaryEmission = "Succeeded";
            }
            catch
            {
                SummaryEmission = submitted ? "Failed" : "Unverified";
                try
                {
                    _owner._failureSink($"TraceId={TraceId};AttemptId={Id};Processor={Processor};FailureCategory={(_collectorFailed ? "Collection" : "Emission")};SummaryIntegrity=Unknown");
                    FallbackEmission = "Succeeded";
                }
                catch { FallbackEmission = "Failed"; }
            }
            finally
            {
                try
                {
                    _owner._acknowledgementSink?.Invoke($"TraceId={TraceId};AttemptId={Id};SummaryEmission={SummaryEmission};FallbackEmission={FallbackEmission};SummaryDurability=Unknown;Delivery={(SummaryEmission == "Succeeded" ? "Accepted" : "Unverified")}");
                }
                catch { /* Delivery acknowledgements are best effort, not durability proof. */ }
                AmbientAttempt.Value = _previous;
            }
        }
        internal sealed class Operation(Attempt owner, string category, long id)
        {
            private readonly long _started = Stopwatch.GetTimestamp();
            private int _finished;
            internal void Finish(string outcome)
            {
                if (Interlocked.Exchange(ref _finished, 1) != 0) return;
                lock (owner._sync)
                {
                    var c = owner.Get(category); c.Active--;
                    if (outcome == "Cancelled") c.Cancelled++;
                    else if (outcome == "Failed") c.Failed++;
                    else c.Completed++;
                    c.ElapsedMs += Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
                    owner._active.Remove(id);
                }
            }
        }
        private sealed class Counts
        {
            internal long Started, Completed, Failed, Cancelled, Active;
            internal double ElapsedMs;
        }
    }
}
