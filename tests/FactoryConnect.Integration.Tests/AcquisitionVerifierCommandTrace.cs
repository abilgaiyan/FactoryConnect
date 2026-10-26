using System.Text.Json;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace FactoryConnect.Integration.Tests;

// This sink only writes test output. It must never execute diagnostic SQL from a
// verifier callback: ExecuteReader may already have returned an active reader.
internal sealed class AcquisitionVerifierCommandTrace : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly SqlConnection _connection;
    private readonly SqlTransaction _transaction;
    private readonly int _lockTimeout;
    private readonly AcquisitionEvidenceCommandDiagnostics.Subscription _subscription;
    private string _lastCompleted = "none";
    private string _lastEvent = "none";
    private bool _allCommandsOwned = true;
    private int _commandsStarted;
    internal bool AllCommandsOwned { get { Drain(); return _allCommandsOwned; } }
    internal int CommandsStarted { get { Drain(); return _commandsStarted; } }

    internal AcquisitionVerifierCommandTrace(ITestOutputHelper output, SqlConnection connection,
        SqlTransaction transaction, int capturedLockTimeout)
    {
        _output = output; _connection = connection; _transaction = transaction; _lockTimeout = capturedLockTimeout;
        output.WriteLine("VERIFIER_TRACE_BEGIN {0}", JsonSerializer.Serialize(new
        {
            SessionId = connection.ServerProcessId,
            connection.ClientConnectionId,
            CapturedLockTimeout = capturedLockTimeout,
            transaction.IsolationLevel,
            AssemblyMvid = typeof(SqlServerObservationIngestionStore).Assembly.ManifestModule.ModuleVersionId
        }));
        _subscription = AcquisitionEvidenceCommandDiagnostics.Subscribe();
    }

    private void Record(AcquisitionEvidenceCommandDiagnostics.CommandEvent item)
    {
        var connectionMatches = ReferenceEquals(item.Connection, _connection);
        var transactionMatches = ReferenceEquals(item.Transaction, _transaction);
        _allCommandsOwned &= connectionMatches && transactionMatches;
        if (item.Outcome == "start") _commandsStarted++;
        _lastEvent = item.Stage + "/" + item.Phase + "/" + item.Outcome + "/" + item.CommandId;
        _output.WriteLine("VERIFIER_COMMAND {0}", JsonSerializer.Serialize(new
        {
            item.Timestamp,
            item.CommandId, item.Stage, item.Phase, item.Outcome,
            item.ElapsedMilliseconds, item.ExecuteMilliseconds, item.RowsRead,
            item.SessionId, item.ClientConnectionId, item.CommandTimeout,
            CapturedLockTimeout = _lockTimeout,
            item.Boundary, item.BatchSize,
            ConnectionMatches = connectionMatches, TransactionMatches = transactionMatches,
            MostRecentCompletedStage = _lastCompleted
        }));
        if (item.Outcome == "completed") _lastCompleted = item.Stage + "/" + item.CommandId;
        if (item.Error is not null) WriteException(item.Error, "command");
    }

    private void Drain()
    {
        while (_subscription.Events.TryDequeue(out var item)) Record(item);
    }

    internal void ReportException(Exception error, string origin = "verifier")
    {
        Drain();
        WriteException(error, origin);
    }

    private void WriteException(Exception error, string origin)
    {
        _output.WriteLine("VERIFIER_EXCEPTION origin={0} lastEvent={1} mostRecentCompletedStage={2} sqlNumber={3}",
            origin, _lastEvent, _lastCompleted, (error as SqlException)?.Number);
        if (error is SqlException sql)
            foreach (SqlError item in sql.Errors)
                _output.WriteLine("SQL_ERROR number={0} state={1} class={2} line={3}", item.Number, item.State, item.Class, item.LineNumber);
        _output.WriteLine("VERIFIER_EXCEPTION_STACK {0}", error.ToString());
    }

    public void Dispose()
    {
        _subscription.Dispose();
        Drain();
        _output.WriteLine("VERIFIER_TRACE_END commandsStarted={0} lastEvent={1} mostRecentCompletedStage={2} allCommandsOwned={3}",
            _commandsStarted, _lastEvent, _lastCompleted, _allCommandsOwned);
    }
}
