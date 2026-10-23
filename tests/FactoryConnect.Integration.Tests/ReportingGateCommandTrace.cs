using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace FactoryConnect.Integration.Tests;

// Test-only observation. No SQL text, parameters or connection strings are emitted.
internal sealed class ReportingGateCommandTrace : IObserver<DiagnosticListener>,
    IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private readonly string _application;
    private readonly ConcurrentDictionary<SqlCommand, long> _pending = new();
    private readonly ConcurrentQueue<Entry> _entries = new();
    private readonly ConcurrentBag<IDisposable> _subscriptions = new();
    private readonly IDisposable _all;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Entry>> _gates = new();
    private readonly ConcurrentDictionary<Guid, long> _commits = new();

    internal Task<Entry> ExclusiveGateAsync(string role) => _gates.GetOrAdd(role + "|Exclusive",
        static _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    private static object? Property(object? value, string name) =>
        value?.GetType().GetProperty(name)?.GetValue(value);

    internal Task<Entry> GateAsync(string role) => _gates.GetOrAdd(role,
        static _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    internal ReportingGateCommandTrace(string application)
    {
        _application = application;
        _all = DiagnosticListener.AllListeners.Subscribe(this);
    }

    internal sealed record Entry(string Role, string Kind, double Milliseconds, long StartedAt, long CompletedAt, bool Failed, string? LockMode,
        int SessionId = 0, Guid ClientConnectionId = default, string? TransactionId = null, Guid OperationId = default);
    internal Entry[] Entries => _entries.ToArray();

    public void OnNext(DiagnosticListener value)
    {
        if (value.Name == "SqlClientDiagnosticListener")
            _subscriptions.Add(value.Subscribe(this));
    }

    public void OnNext(KeyValuePair<string, object?> value)
    {
        if (value.Key.Contains("WriteTransactionCommit", StringComparison.Ordinal))
        {
            var connection = Property(value.Value, "Connection") as SqlConnection;
            if (connection is null) return;
            var name = new SqlConnectionStringBuilder(connection.ConnectionString).ApplicationName;
            if (!name.StartsWith(_application + ":", StringComparison.Ordinal)) return;
            var operation = Property(value.Value, "OperationId") is Guid id ? id : Guid.Empty;
            if (value.Key.EndsWith("Before", StringComparison.Ordinal))
            {
                _commits[operation] = Stopwatch.GetTimestamp();
                return;
            }
            if (!_commits.TryRemove(operation, out var began)) return;
            var finished = Stopwatch.GetTimestamp();
            _entries.Enqueue(new Entry(name[(_application.Length + 1)..], "transaction-commit",
                Stopwatch.GetElapsedTime(began, finished).TotalMilliseconds, began, finished,
                value.Key.EndsWith("Error", StringComparison.Ordinal), null,
                connection.State == System.Data.ConnectionState.Open ? connection.ServerProcessId : 0, connection.ClientConnectionId,
                Convert.ToString(Property(value.Value, "TransactionId"), System.Globalization.CultureInfo.InvariantCulture),
                operation));
            return;
        }
        var command = Property(value.Value, "Command") as SqlCommand;
        if (command?.Connection is null) return;
        var application = new SqlConnectionStringBuilder(command.Connection.ConnectionString).ApplicationName;
        if (!application.StartsWith(_application + ":", StringComparison.Ordinal)) return;
        if (value.Key.EndsWith("WriteCommandBefore", StringComparison.Ordinal))
        {
            _pending[command] = Stopwatch.GetTimestamp();
            return;
        }
        if (!value.Key.EndsWith("WriteCommandAfter", StringComparison.Ordinal) &&
            !value.Key.EndsWith("WriteCommandError", StringComparison.Ordinal)) return;
        if (!_pending.TryRemove(command, out var started)) return;
        var ended = Stopwatch.GetTimestamp();
        var sql = command.CommandText;
        var kind = sql.Contains("sp_getapplock", StringComparison.Ordinal) ? "gate" :
            sql.Contains("OperationalMetricProjectionCheckpoint", StringComparison.Ordinal) ?
                (sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) ||
                 sql.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) ? "checkpoint-write-sql" : "checkpoint-sql") :
            sql.Contains("OperationalMetricProjectionEvidence", StringComparison.Ordinal) ? "evidence-sql" :
            sql.Contains("PublicationResultArchive", StringComparison.Ordinal) ? "result-archive-sql" :
            sql.Contains("ConflictWitnessArchive", StringComparison.Ordinal) ? "witness-archive-sql" : "other-sql";
        var entry = new Entry(application[(_application.Length + 1)..], kind,
            Stopwatch.GetElapsedTime(started, ended).TotalMilliseconds, started, ended,
            value.Key.EndsWith("WriteCommandError", StringComparison.Ordinal),
            kind == "gate" ? Convert.ToString(command.Parameters["@Mode"].Value,
                System.Globalization.CultureInfo.InvariantCulture) : null,
            command.Connection.State == System.Data.ConnectionState.Open ? command.Connection.ServerProcessId : 0, command.Connection.ClientConnectionId,
            Convert.ToString(Property(value.Value, "TransactionId"), System.Globalization.CultureInfo.InvariantCulture),
            Property(value.Value, "OperationId") is Guid operationId ? operationId : Guid.Empty);
        _entries.Enqueue(entry);
        if (kind == "gate")
        {
            _gates.GetOrAdd(entry.Role,
                static _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(entry);
            if (entry.LockMode == "Exclusive")
                _gates.GetOrAdd(entry.Role + "|Exclusive",
                    static _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(entry);
        }
    }

    internal void Write(ITestOutputHelper output)
    {
        foreach (var gate in Entries.Where(e => e.Kind == "gate").OrderBy(e => e.StartedAt))
            output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                measurement = "gate-command", gate.Role, gate.LockMode, gate.Milliseconds,
                gate.StartedAt, gate.CompletedAt, gate.Failed,
                gate.SessionId, gate.ClientConnectionId, gate.TransactionId, gate.OperationId,
            }));
        foreach (var entry in Entries.Where(e => e.Kind != "gate" &&
                     (e.Kind is "transaction-commit" or "checkpoint-sql" or "checkpoint-write-sql" || e.Milliseconds >= 100))
                     .OrderBy(e => e.StartedAt))
            output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                measurement = "sql-operation", entry.Role, entry.Kind, entry.Milliseconds,
                entry.StartedAt, entry.CompletedAt, entry.Failed, entry.SessionId,
                entry.ClientConnectionId, entry.TransactionId, entry.OperationId,
            }));
        foreach (var group in Entries.GroupBy(e => (e.Role, e.Kind)))
            output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                measurement = "sql-execution", role = group.Key.Role, kind = group.Key.Kind,
                commands = group.Count(), milliseconds = group.Sum(e => e.Milliseconds),
                failures = group.Count(e => e.Failed),
            }));
        output.WriteLine("SQL timings end at Execute return; row streaming, CPU verification and transaction completion are outside these sums.");
    }

    internal static string Connection(string source, string application, string role) =>
        new SqlConnectionStringBuilder(source) { ApplicationName = application + ":" + role }.ConnectionString;

    public void OnCompleted() { }
    public void OnError(Exception error) { }
    public void Dispose()
    {
        _all.Dispose();
        foreach (var subscription in _subscriptions) subscription.Dispose();
    }
}
