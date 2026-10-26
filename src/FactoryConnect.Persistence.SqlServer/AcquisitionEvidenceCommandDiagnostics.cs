using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

// Passive, invocation-local diagnostic subscription. No SQL, session settings,
// command timeout, retry, isolation or publication behavior is changed here.
internal static class AcquisitionEvidenceCommandDiagnostics
{
    private static readonly AsyncLocal<Subscription?> Observer = new();

    internal sealed record CommandEvent(Guid CommandId, DateTimeOffset Timestamp, string Stage, string Phase, string Outcome,
        double ElapsedMilliseconds, double? ExecuteMilliseconds, long RowsRead, int SessionId,
        Guid ClientConnectionId, int CommandTimeout, ulong? Boundary, int? BatchSize,
        SqlConnection Connection, SqlTransaction? Transaction, Exception? Error);

    internal sealed class Subscription : IDisposable
    {
        private readonly Subscription? _previous;
        internal ConcurrentQueue<CommandEvent> Events { get; } = new();
        internal Subscription()
        { _previous = Observer.Value; Observer.Value = this; }
        public void Dispose() => Observer.Value = _previous;
    }

    internal static Subscription Subscribe() => new();

    internal static CommandScope? Start(SqlCommand command, string stage, ulong? boundary = null, int? batchSize = null) =>
        Observer.Value is { } observer ? new CommandScope(command, stage, boundary, batchSize, observer) : null;

    internal sealed class CommandScope : IDisposable
    {
        private readonly Subscription _observer;
        private readonly Guid _id = Guid.NewGuid();
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly SqlConnection _connection;
        private readonly SqlTransaction? _transaction;
        private readonly string _stage;
        private readonly ulong? _boundary;
        private readonly int? _batchSize;
        private readonly int _session;
        private readonly Guid _clientConnection;
        private readonly int _timeout;
        private string _phase = "execute";
        private double? _executeMilliseconds;
        private long _rows;
        private bool _ended;

        internal CommandScope(SqlCommand command, string stage, ulong? boundary, int? batchSize, Subscription observer)
        {
            _observer = observer; _connection = command.Connection!; _transaction = command.Transaction;
            _stage = stage; _boundary = boundary; _batchSize = batchSize;
            // Provider properties only, captured before execution. Never query an active reader.
            _session = _connection.ServerProcessId; _clientConnection = _connection.ClientConnectionId;
            _timeout = command.CommandTimeout;
            Emit("start");
        }

        internal void ExecuteReturned()
        {
            _executeMilliseconds = Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
            Emit("execute-returned"); _phase = "result-consumption";
        }
        internal void RowRead() => _rows++;
        internal void ConsumptionFinished() { Emit("rows-consumed"); _phase = "reader-dispose"; }
        internal void Completed() { if (_ended) return; _ended = true; Emit("completed"); }
        internal void Failed(Exception error)
        {
            if (_ended) return;
            if (_phase == "execute") _executeMilliseconds = Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
            _ended = true; Emit("failed", error);
        }
        public void Dispose() { if (_ended) return; _ended = true; Emit("early-exit"); }
        private void Emit(string outcome, Exception? error = null) => _observer.Events.Enqueue(new CommandEvent(_id, DateTimeOffset.UtcNow, _stage, _phase,
            outcome, Stopwatch.GetElapsedTime(_started).TotalMilliseconds, _executeMilliseconds, _rows,
            _session, _clientConnection, _timeout, _boundary, _batchSize, _connection, _transaction, error));
    }
}
