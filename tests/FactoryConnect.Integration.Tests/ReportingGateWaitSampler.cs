using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace FactoryConnect.Integration.Tests;

// Test-only, sampled evidence. The journal survives a failed test/cleanup.
internal sealed class ReportingGateWaitSampler(string connectionString, string application,
    Func<string>? phaseSnapshot = null) : IDisposable
{
    private readonly ConcurrentQueue<string> _samples = new();
    private readonly ConcurrentQueue<string> _events = new();
    private readonly ConcurrentQueue<long> _successfulSamples = new();
    private readonly ConcurrentQueue<long> _criticalObservations = new();
    private readonly List<long> _currentCriticalObservations = [];
    private int _sampleCount;
    private int _rows;
    private long _started;
    private long _finished;
    private long _lastSuccess;
    private long _operationStarted;
    private string _stage = "not-started";
    private StreamWriter? _journal;
    private FileStream? _journalStream;
    internal string JournalPath { get; } = Path.GetFullPath(Path.Combine(
        "artifacts", "TestResults", "Gg003GateSampler", application + ".jsonl"));
    internal string? FailureRecord { get; private set; }
    internal string[] Events => _events.ToArray();
    internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal async Task RunAsync(CancellationToken token)
    {
        await using var connection = new SqlConnection(connectionString);
        await RunCoreAsync(ct => connection.OpenAsync(ct), ct => SampleAsync(connection, ct), token);
    }

    // Same lifecycle, journal and failure handling; delegates allow database-free failure tests.
    internal async Task RunCoreAsync(Func<CancellationToken, Task> open,
        Func<CancellationToken, Task> sample, CancellationToken token)
    {
        _started = Stopwatch.GetTimestamp();
        try
        {
            SetStage("journal-open");
            Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
            _journalStream = new FileStream(JournalPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            _journal = new StreamWriter(_journalStream) { AutoFlush = true };
            Record(new { measurement = "sampler-start", at = _started, phase = Phase(), journal = JournalPath,
                queryTimeoutSeconds = 5, nominalIntervalMs = 250 });
            SetStage("connection-open");
            Record(new { measurement = "sampler-operation-start", stage = _stage, at = _operationStarted, phase = Phase() });
            await open(token);
            Record(new { measurement = "sampler-connection-open", startedAt = _operationStarted,
                completedAt = Stopwatch.GetTimestamp(), milliseconds = Elapsed(_operationStarted), phase = Phase() });
            long previousCompletion = Stopwatch.GetTimestamp();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                SetStage("dmv-query");
                Record(new { measurement = "sampler-query-start", sequence = _sampleCount + 1,
                    at = _operationStarted, phase = Phase(),
                    sincePreviousCompletionMs = Stopwatch.GetElapsedTime(previousCompletion, _operationStarted).TotalMilliseconds });
                var queryStarted = _operationStarted;
                _currentCriticalObservations.Clear();
                await sample(token);
                var completed = Stopwatch.GetTimestamp();
                Record(new { measurement = "sampler-query-complete", sequence = _sampleCount + 1,
                    startedAt = queryStarted, completedAt = completed,
                    milliseconds = Stopwatch.GetElapsedTime(queryStarted, completed).TotalMilliseconds, phase = Phase() });
                _successfulSamples.Enqueue(completed);
                foreach (var observedAt in _currentCriticalObservations) _criticalObservations.Enqueue(observedAt);
                _sampleCount++;
                _lastSuccess = completed;
                previousCompletion = completed;
                Ready.TrySetResult();
                SetStage("sampling-delay");
                await Task.Delay(TimeSpan.FromMilliseconds(250), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Ready.TrySetCanceled(token);
            Record(new { measurement = "sampler-cancelled", stage = _stage, at = Stopwatch.GetTimestamp(),
                phase = Phase(), interruptedQuery = _stage != "sampling-delay" });
        }
        catch (Exception error)
        {
            // Record before propagation, without SQL text, messages or parameter values.
            var now = Stopwatch.GetTimestamp();
            FailureRecord = JsonSerializer.Serialize(new
            {
                measurement = "sampler-failure", stage = _stage, phase = Phase(),
                exceptionType = error.GetType().FullName, sqlErrorNumber = (error as SqlException)?.Number,
                innerExceptionType = error.InnerException?.GetType().FullName,
                startedAt = _operationStarted, failedAt = now,
                operationElapsedMs = Stopwatch.GetElapsedTime(_operationStarted, now).TotalMilliseconds,
                samplerElapsedMs = Stopwatch.GetElapsedTime(_started, now).TotalMilliseconds,
                successfulSamples = _sampleCount, observedRows = _rows, lastSuccessfulSampleAt = _lastSuccess,
            });
            _events.Enqueue(FailureRecord);
            try { _journal?.WriteLine(FailureRecord); _journalStream?.Flush(flushToDisk: true); }
            catch (Exception journalError)
            {
                _events.Enqueue(JsonSerializer.Serialize(new { measurement = "sampler-journal-failure",
                    exceptionType = journalError.GetType().FullName, at = Stopwatch.GetTimestamp() }));
            }
            Ready.TrySetException(error);
            throw; // An incomplete monitor remains failed diagnostic acceptance.
        }
        finally
        {
            _finished = Stopwatch.GetTimestamp();
            try
            {
                Record(new { measurement = "sampler-stop", at = _finished, phase = Phase(),
                    failed = FailureRecord is not null, successfulSamples = _sampleCount,
                    lastSuccessfulSampleAt = _lastSuccess,
                    sinceLastSuccessMs = _lastSuccess == 0 ? (double?)null :
                        Stopwatch.GetElapsedTime(_lastSuccess, _finished).TotalMilliseconds });
                _journalStream?.Flush(flushToDisk: true);
            }
            catch (Exception journalError) when (FailureRecord is not null)
            {
                _events.Enqueue(JsonSerializer.Serialize(new { measurement = "sampler-journal-finalization-failure",
                    exceptionType = journalError.GetType().FullName, at = Stopwatch.GetTimestamp() }));
            }
            finally
            {
                try { _journal?.Dispose(); }
                catch (Exception journalError) when (FailureRecord is not null)
                {
                    _events.Enqueue(JsonSerializer.Serialize(new { measurement = "sampler-journal-close-failure",
                        exceptionType = journalError.GetType().FullName, at = Stopwatch.GetTimestamp() }));
                }
                finally { _journalStream?.Dispose(); }
            }
        }
    }

    private async Task SampleAsync(SqlConnection connection, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 5; // Unchanged diagnostic timeout; production settings are untouched.
        command.CommandText = """
            SELECT s.session_id,s.program_name,s.login_time AS session_login_time,c.connection_id AS server_connection_id,r.request_id,
                   r.status,r.wait_type,r.wait_time,r.blocking_session_id,
                   r.transaction_id AS server_transaction_id,st.transaction_id AS server_session_transaction_id,
                   s.open_transaction_count,l.resource_description,l.request_mode,
                   l.request_status,l.request_owner_id AS server_lock_owner_id
            FROM sys.dm_exec_sessions s
            LEFT JOIN sys.dm_exec_connections c ON c.session_id=s.session_id
            LEFT JOIN sys.dm_exec_requests r ON r.session_id=s.session_id
            LEFT JOIN sys.dm_tran_session_transactions st ON st.session_id=s.session_id
            LEFT JOIN sys.dm_tran_locks l ON l.request_session_id=s.session_id
                 AND l.resource_type='APPLICATION' AND l.resource_database_id=DB_ID()
            WHERE LEFT(s.program_name,LEN(@Prefix))=@Prefix
            ORDER BY s.session_id,r.request_id;
            """;
        command.Parameters.AddWithValue("@Prefix", application + ":");
        var began = Stopwatch.GetTimestamp();
        await using var rows = await command.ExecuteReaderAsync(token);
        Record(new { measurement = "sampler-query-execute-return", startedAt = began,
            completedAt = Stopwatch.GetTimestamp(), milliseconds = Elapsed(began), phase = Phase() });
        SetStage("dmv-row-stream");
        var streamStarted = Stopwatch.GetTimestamp();
        var count = 0;
        while (await rows.ReadAsync(token))
        {
            var observedAt = Stopwatch.GetTimestamp();
            if (Convert.ToString(rows["program_name"], System.Globalization.CultureInfo.InvariantCulture)?.EndsWith(":initial-checkpoint", StringComparison.Ordinal) == true)
                _currentCriticalObservations.Add(observedAt);
            var data = new Dictionary<string, object?>
            {
                ["measurement"] = "sql-wait-sample", ["sampleStartedAt"] = began,
                ["observedAt"] = observedAt, ["phase"] = Phase(),
            };
            for (var i = 0; i < rows.FieldCount; i++)
                data[rows.GetName(i)] = rows.IsDBNull(i) ? null : rows.GetValue(i);
            var json = JsonSerializer.Serialize(data);
            _rows++; count++;
            if (_samples.Count < 5000) _samples.Enqueue(json);
            _journal!.WriteLine(json); // All rows in journal, even after console cap.
        }
        Record(new { measurement = "sampler-row-stream-complete", startedAt = streamStarted,
            completedAt = Stopwatch.GetTimestamp(), milliseconds = Elapsed(streamStarted), rows = count, phase = Phase() });
    }

    private string Phase() => phaseSnapshot?.Invoke() ?? "unspecified";
    private void SetStage(string stage) { _stage = stage; _operationStarted = Stopwatch.GetTimestamp(); }
    private static double Elapsed(long started) => Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    private void Record(object value)
    {
        var json = JsonSerializer.Serialize(value);
        _events.Enqueue(json);
        _journal?.WriteLine(json);
    }


    internal ReportingGateCriticalCoverage.Assessment AssessCriticalCoverage(long started, long ended) =>
        ReportingGateCriticalCoverage.Assess(started, ended, _successfulSamples.ToArray(), _criticalObservations.ToArray());

    // Called after RunAsync is observed and its journal is closed. Failure remains explicit.
    internal bool PersistCriticalCoverage(ReportingGateCriticalCoverage.Assessment assessment)
    {
        var json = JsonSerializer.Serialize(new { measurement = "critical-phase-coverage",
            phase = "initial-checkpoint-read", assessment });
        _events.Enqueue(json);
        try
        {
            using var stream = new FileStream(JournalPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8, 1024, leaveOpen: true);
            writer.WriteLine(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
            return true;
        }
        catch (Exception error)
        {
            _events.Enqueue(JsonSerializer.Serialize(new { measurement = "coverage-journal-failure",
                exceptionType = error.GetType().FullName, at = Stopwatch.GetTimestamp() }));
            return false;
        }
    }

    public void Dispose()
    {
        try { _journal?.Dispose(); }
        finally
        {
            _journalStream?.Dispose();
            _journal = null;
            _journalStream = null;
        }
    }

    internal void Write(ITestOutputHelper output)
    {
        var reportedAt = Stopwatch.GetTimestamp();
        foreach (var sample in _samples) output.WriteLine(sample);
        foreach (var entry in _events) output.WriteLine(entry);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            measurement = "sql-wait-sampling-coverage", startedAt = _started, finishedAt = _finished,
            samples = _sampleCount, observedRows = _rows, retainedRows = _samples.Count,
            nominalIntervalMs = 250, truncated = _rows > _samples.Count, failed = FailureRecord is not null,
            lastSuccessfulSampleAt = _lastSuccess, reportedAt,
            unobservedTailMs = _lastSuccess == 0 ? (double?)null :
                Stopwatch.GetElapsedTime(_lastSuccess, reportedAt).TotalMilliseconds,
            sinceSamplerStoppedMs = _finished == 0 ? (double?)null :
                Stopwatch.GetElapsedTime(_finished, reportedAt).TotalMilliseconds,
            journal = JournalPath,
        }));
        output.WriteLine("DMV query execution, row streaming and scheduling intervals are separate observations, not proof of server-side cause. Missing samples cannot exclude blocking. Journal I/O adds observer overhead.");
    }
}
