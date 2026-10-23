using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

// Observes actual SqlClient Execute calls, not cache requests. No production hook or SQL change.
// Each instance owns a unique application name so parallel fixtures cannot affect its counts.
internal sealed class ContainmentHistoricalSqlCounter : IObserver<DiagnosticListener>,
    IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private readonly string _application = "FC-HistoricalCount:" + Guid.NewGuid().ToString("N");
    private readonly ConcurrentDictionary<SqlCommand, Execution> _pending = new();
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
    internal sealed record Execution(SqlTransaction? Transaction, long Processor, long Stream, ulong Position, bool Failed = false);
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
            var execution = new Execution(command.Transaction,
                Convert.ToInt64(command.Parameters["@ProcessorRowId"].Value, CultureInfo.InvariantCulture),
                Convert.ToInt64(command.Parameters["@StreamRowId"].Value, CultureInfo.InvariantCulture),
                Convert.ToUInt64(command.Parameters["@ThroughPosition"].Value, CultureInfo.InvariantCulture));
            if (!_pending.TryAdd(command, execution))
                ListenerError = new InvalidOperationException("Duplicate historical SQL start event.");
        }
        else if (value.Key.EndsWith("WriteCommandAfter", StringComparison.Ordinal) ||
                 value.Key.EndsWith("WriteCommandError", StringComparison.Ordinal))
        {
            if (_pending.TryRemove(command, out var execution))
                _completed.Enqueue(execution with { Failed = value.Key.EndsWith("WriteCommandError", StringComparison.Ordinal) });
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
