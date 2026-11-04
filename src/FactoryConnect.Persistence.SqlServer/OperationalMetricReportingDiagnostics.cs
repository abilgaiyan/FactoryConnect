using System.Diagnostics;
using System.Runtime.CompilerServices;
using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>Observational, listener-controlled diagnostics. Never records SQL or parameter values.</summary>
internal static class OperationalMetricReportingDiagnostics
{
    internal static readonly ActivitySource Source = new("FactoryConnect.Reporting");
    private static readonly ConditionalWeakTable<Activity, Budget> Budgets = new();
    private const int MaximumSpans = 4096;

    internal sealed class Scope : IDisposable
    {
        private Activity? _activity;
        private readonly ReportingDiagnosticSession.Attempt.Operation? _operation;
        private readonly ReportingDiagnosticSession.Attempt? _attempt;
        private int _disposed;
        private string _outcome = "Completed";
        internal Scope(Activity? activity, ReportingDiagnosticSession.Attempt.Operation? operation = null,
            ReportingDiagnosticSession.Attempt? attempt = null)
        { _activity = activity; _operation = operation; _attempt = attempt; }
        internal void Tag(string key, object? value)
        {
            try { _attempt?.Tag(key, value); _activity?.SetTag(key, value); } catch { /* Diagnostics cannot change verification. */ }
        }
        internal bool IncludeIdentity
        {
            get
            {
                try
                {
                    for (var current = _activity; current is not null; current = current.Parent)
                        if (current.GetTagItem("diagnostic.capture.identity") is true) return true;
                }
                catch { /* Diagnostics only. */ }
                return false;
            }
        }
        internal void Fail(Exception error, CancellationToken token = default) =>
            _outcome = error is OperationCanceledException || (error is SqlException && token.IsCancellationRequested)
                ? "Cancelled" : "Failed";
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _operation?.Finish(_outcome); }
            catch { _attempt?.Degrade(); }
            try { _activity?.SetTag("diagnostic.outcome", _outcome); _activity?.Dispose(); }
            catch { /* Preserve authoritative result/exception. */ }
            finally { _activity = null; }
        }
    }

    internal static Scope Start(string operation)
    {
        var attempt = ReportingDiagnosticSession.CurrentAttempt;
        ReportingDiagnosticSession.Attempt.Operation? accounting = null;
        try
        {
            accounting = attempt?.Start(operation);
            var session = ReportingDiagnosticSession.Current;
            if (session is not null)
            {
                var admitted = session.Admit(attempt?.Processor);
                attempt?.Detail(admitted);
                if (!admitted)
                {
                    var captureRoot = Activity.Current;
                    while (captureRoot?.Parent is { } captureParent) captureRoot = captureParent;
                    captureRoot?.SetTag("diagnostic.capture.truncated", true);
                    return new(null, accounting, attempt);
                }
                // Admission is consumed even if sampling declines Activity creation.
                return new(Source.StartActivity(operation), accounting, attempt);
            }
            if (!Source.HasListeners()) return new(null);
            var root = Activity.Current;
            while (root?.Parent is { } parent) root = parent;
            if (root is not null && Interlocked.Increment(ref Budgets.GetValue(root, static _ => new()).Count) > MaximumSpans)
            {
                root.SetTag("diagnostic.capture.truncated", true);
                return new(null);
            }
            return new(Source.StartActivity(operation));
        }
        catch { attempt?.Degrade(); return new(null, accounting, attempt); }
    }

    internal static async Task OpenAsync(SqlConnection connection, CancellationToken token)
    {
        using var scope = Start("reporting.connection.open");
        try { await connection.OpenAsync(token).ConfigureAwait(false); }
        catch (Exception error) { scope.Fail(error, token); throw; }
    }

    internal static async Task<object?> ScalarAsync(SqlCommand command, string operation, CancellationToken token)
    {
        using var scope = Start("reporting.sql.execute");
        scope.Tag("diagnostic.operation", operation);
        scope.Tag("diagnostic.command.count", 1);
        try { return await command.ExecuteScalarAsync(token).ConfigureAwait(false); }
        catch (Exception error) { scope.Fail(error, token); throw; }
    }

    private static readonly ConditionalWeakTable<SqlDataReader, ReaderLease> Readers = new();
    internal static async Task<ReaderLease> ReaderAsync(SqlCommand command, string operation, CancellationToken token)
    {
        using var scope = Start("reporting.sql.execute");
        scope.Tag("diagnostic.operation", operation);
        scope.Tag("diagnostic.command.count", 1);
        try
        {
            var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            scope.Dispose();
            var lease = new ReaderLease(reader, operation);
            try { Readers.Add(reader, lease); } catch { /* Instrumentation only. */ }
            return lease;
        }
        catch (Exception error) { scope.Fail(error, token); throw; }
    }

    internal static async Task<bool> ReadAsync(SqlDataReader reader, CancellationToken token)
    {
        Readers.TryGetValue(reader, out var lease);
        try
        {
            var result = await reader.ReadAsync(token).ConfigureAwait(false);
            if (result && lease is not null) lease.Rows++;
            return result;
        }
        catch (Exception error) { lease?.Consumption.Fail(error, token); throw; }
    }

    internal sealed class ReaderLease : IAsyncDisposable
    {
        internal SqlDataReader Reader { get; }
        internal Scope Consumption { get; }
        internal long Rows;
        internal HistoricalLoadDiagnostics? HistoricalLoad { get; set; }
        internal CancellationToken CleanupToken { get; set; }
        internal ReaderLease(SqlDataReader reader, string operation)
        {
            Reader = reader;
            Consumption = Start("reporting.sql.consume");
            Consumption.Tag("diagnostic.operation", operation);
        }
        public async ValueTask DisposeAsync()
        {
            Consumption.Tag("diagnostic.row.count", Rows);
            Consumption.Dispose();
            using var cleanup = Start("reporting.sql.cleanup");
            var cleanupStarted = HistoricalLoad is null ? 0 : Stopwatch.GetTimestamp();
            Exception? cleanupError = null;
            try { await Reader.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { cleanupError = error; cleanup.Fail(error); throw; }
            finally
            {
                try { HistoricalLoad?.Cleanup(cleanupStarted, cleanupError, CleanupToken); }
                catch { /* Diagnostics cannot replace disposal failure. */ }
                Readers.Remove(Reader);
            }
        }
    }

    internal static void Evaluation(Scope scope, OperationalMetricEvaluationKey key, MetricAggregationCheckpoint cut)
    {
        try
        {
            ReportingDiagnosticSession.CurrentAttempt?.Checkpoint(cut);
            if (!Source.HasListeners()) return;
            using var bytes = new MemoryStream();
            using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write("FC-REPORTING-EVALUATION/1");
                var encoded = OperationalMetricEvaluationKeyV1Codec.Encode(key);
                writer.Write(encoded.Length); writer.Write(encoded);
                writer.Write(AvailabilityContainmentV1.ProjectionProcessorId(key.MachineId).Value);
                writer.Write(cut.ProcessorId.Value); writer.Write(cut.StreamId.MachineId.Value.ToByteArray());
                writer.Write(cut.StreamId.StreamKey); writer.Write(cut.Position.Value);
                writer.Write(AvailabilityContainmentV1.PolicyId); writer.Write(2);
            }
            if (scope.IncludeIdentity) scope.Tag("restricted.identity.canonical", Convert.ToHexString(bytes.ToArray()));
            scope.Tag("diagnostic.identity.schema", "FC-REPORTING-EVALUATION/1");
            scope.Tag("diagnostic.evaluation.fingerprint", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes.ToArray())));
        }
        catch { /* Correlation cannot change evaluation. */ }
    }
    private sealed class Budget { internal int Count; }
}
