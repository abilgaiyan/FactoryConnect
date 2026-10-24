using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

// Observes actual SqlClient Execute calls, not cache requests. No production hook or SQL change.
// Each instance owns a unique application name so parallel fixtures cannot affect its counts.
internal sealed class ContainmentHistoricalSqlCounter : IObserver<DiagnosticListener>,
    IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private readonly string _application = "FC-HistoricalCount:" + Guid.NewGuid().ToString("N");
    private sealed record PendingExecution(Execution Execution, long StartedAt);

    private readonly ConcurrentDictionary<SqlCommand, PendingExecution> _pending = new();
    private sealed record TransactionIdentity(long Ordinal);
    private readonly ConditionalWeakTable<object, TransactionIdentity> _transactions = new();
    private long _nextTransactionOrdinal;
    private readonly ConcurrentQueue<Execution> _completed = new();
    private readonly ConcurrentBag<IDisposable> _subscriptions = new();
    private readonly IDisposable _all;

    internal ContainmentHistoricalSqlCounter(string connectionString)
    {
        ConnectionString = new SqlConnectionStringBuilder(connectionString)
        { ApplicationName = _application, Pooling = false }.ConnectionString;
        _all = DiagnosticListener.AllListeners.Subscribe(this);
    }

    internal string ConnectionString { get; }
    internal sealed record Execution(long TransactionOrdinal, bool HadTransactionAtStart,
        bool TransactionBoundToConnectionAtStart, long Processor, long Stream, ulong Position,
        bool Failed = false, TimeSpan? Elapsed = null);

    internal sealed record TransactionStart(long Ordinal, bool HadTransaction, bool BoundToConnection);

    // Pure identity seam for database-free regressions. Production supplies the actual
    // command transaction and its Connection exactly once at the Before event.
    internal TransactionStart CaptureTransactionStart(object? transaction, object? connection, object? transactionConnection)
    {
        if (transaction is null) return new(0, false, false); // Invalid/missing, never a grouping identity.
        var identity = _transactions.GetValue(transaction,
            _ => new TransactionIdentity(Interlocked.Increment(ref _nextTransactionOrdinal)));
        return new(identity.Ordinal, true, connection is not null && ReferenceEquals(connection, transactionConnection));
    }
    internal Execution[] Executions => _completed.ToArray();
    internal int PendingCount => _pending.Count;
    internal Exception? ListenerError { get; private set; }

    public void OnNext(DiagnosticListener value)
    {
        if (value.Name == "SqlClientDiagnosticListener") _subscriptions.Add(value.Subscribe(this));
    }

    public void OnNext(KeyValuePair<string, object?> value)
    {
        if (value.Value?.GetType().GetProperty("Command")?.GetValue(value.Value) is not SqlCommand command) return;
        if (value.Key.EndsWith("WriteCommandBefore", StringComparison.Ordinal))
        {
            if (command.Connection is null ||
                new SqlConnectionStringBuilder(command.Connection.ConnectionString).ApplicationName != _application ||
                !command.CommandText.StartsWith("SELECT s.MachineId, s.StreamKey, f.Position, f.FactId, f.MetricInputKey, ", StringComparison.Ordinal) ||
                !command.CommandText.Contains("FROM dbo.MetricAggregationContribution c ", StringComparison.Ordinal) ||
                !command.CommandText.Contains("AND c.Position <= @ThroughPosition ", StringComparison.Ordinal)) return;
            var transaction = command.Transaction;
            var start = CaptureTransactionStart(transaction, command.Connection, transaction?.Connection);
            var execution = new Execution(start.Ordinal, start.HadTransaction, start.BoundToConnection,
                Convert.ToInt64(command.Parameters["@ProcessorRowId"].Value, CultureInfo.InvariantCulture),
                Convert.ToInt64(command.Parameters["@StreamRowId"].Value, CultureInfo.InvariantCulture),
                Convert.ToUInt64(command.Parameters["@ThroughPosition"].Value, CultureInfo.InvariantCulture));
            if (!_pending.TryAdd(command, new PendingExecution(execution, Stopwatch.GetTimestamp())))
            {
                ListenerError = new InvalidOperationException("Duplicate historical SQL start event.");
            }
        }
        else if (value.Key.EndsWith("WriteCommandAfter", StringComparison.Ordinal) ||
                 value.Key.EndsWith("WriteCommandError", StringComparison.Ordinal))
        {
            if (_pending.TryRemove(command, out var pending))
            {
                _completed.Enqueue(pending.Execution with
                {
                    Failed = value.Key.EndsWith("WriteCommandError", StringComparison.Ordinal),
                    Elapsed = Stopwatch.GetElapsedTime(pending.StartedAt)
                });
            }
        }
    }

    public void OnError(Exception error) => ListenerError = error;
    public void OnCompleted() { }

    public void Dispose()
    {
        _all.Dispose();
        foreach (var subscription in _subscriptions) subscription.Dispose();
    }
}
