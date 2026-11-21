using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Text.Json;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
[Collection(Gg003AcquisitionCloneBenchmarkDefinition.CollectionName)]
public sealed class Gg003AcquisitionConcurrencyTests(ITestOutputHelper output)
{
    private const string Prefix = "FACTORYCONNECT_GG003_D1C_";
    private const string Database = "GG003_ACQ_D1C_20261026";

    [D1CTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultTransactionAllowsInteriorMutationAndFreshVerificationRejectsIt(bool delete)
    {
        var f = await SeedAsync();
        await using var a = new SqlConnection(f.Connection);
        await using var b = new SqlConnection(f.Connection);
        await a.OpenAsync(); await b.OpenAsync();
        await using var transaction = (SqlTransaction)await a.BeginTransactionAsync();
        Assert.Equal(2, await DescribeAsync(a, transaction, "A default transaction"));
        // Same checkpoint lock used by ingestion; no extra observation locking hint.
        await ScalarAsync(a, transaction, f.Stream, "SELECT InstanceId FROM dbo.ObservationStreamCheckpoint WITH (UPDLOCK,HOLDLOCK) WHERE MachineId=@M AND StreamKeyBinary=@K;");
        var fetched = await ScalarAsync(a, transaction, f.Stream, "SELECT ObservationValue FROM dbo.MachineObservation WHERE MachineId=@M AND StreamKeyBinary=@K AND Position=1;");
        output.WriteLine("order: source reader fully disposed; begin concurrent mutation B");
        await using var mutationTransaction = (SqlTransaction)await b.BeginTransactionAsync();
        await DescribeAsync(b, mutationTransaction, "B mutation transaction");
        var changed = await ExecuteAsync(b, mutationTransaction, f.Stream, delete
            ? "DELETE dbo.MachineObservation WHERE MachineId=@M AND StreamKeyBinary=@K AND Position=1;"
            : "UPDATE dbo.MachineObservation SET ObservationValue=N'D1C-mutated' WHERE MachineId=@M AND StreamKeyBinary=@K AND Position=1;");
        Assert.Equal(1, changed);
        await mutationTransaction.CommitAsync();
        output.WriteLine("order: B commit completed before A reread");
        var fresh = await ScalarAsync(a, transaction, f.Stream, "SELECT ObservationValue FROM dbo.MachineObservation WHERE MachineId=@M AND StreamKeyBinary=@K AND Position=1;");
        Assert.NotEqual(fetched, fresh);
        Assert.Equal(f.First.Observations[0].Observation.Value, fetched);
        output.WriteLine("counterexample model: retained value matches original; fresh lookup differs. This is not an optimized production implementation result.");
        await transaction.RollbackAsync();
        var store = new SqlServerObservationIngestionStore(f.Connection);
        Assert.Equal(AcquisitionEvidenceReadStatus.Corrupt, (await store.ReadExactAsync(f.Stream, Identity(f.Repeated))).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitAsync(f.Repeated).AsTask());
        var extension = AcquisitionHistoryWorkload.Batch(f.Stream, f.Repeated.Checkpoint, 1);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.CommitAsync(extension).AsTask());
        Assert.Equal(f.Repeated.Checkpoint, await store.ReadCheckpointAsync(f.Stream));
        output.WriteLine("actual baseline: exact=Corrupt; replay=InvalidOperationException; append=InvalidDataException; checkpoint unchanged");
    }

    [D1CTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualSerializableExactReaderBlocksUpdateOrDeleteUntilCommit(bool delete)
    {
        var f = await SeedAsync();
        using var pause = new StorePause(f.Connection, "acquisition.evidence.sources.verify");
        var store = new SqlServerObservationIngestionStore(pause.ConnectionString);
        var read = Task.Run(async () => await store.ReadExactAsync(f.Stream, Identity(f.Repeated), pause.Token));
        await using var b = new SqlConnection(f.Connection);
        Task<int>? mutation = null;
        try
        {
            await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(4, await DescribeAsync(pause.Connection!, pause.Transaction, "A actual exact reader paused after source verification"));
            Assert.Equal(IsolationLevel.Serializable, pause.Transaction!.IsolationLevel);
            await b.OpenAsync();
            mutation = ExecuteAsync(b, null, f.Stream, delete
                ? "DELETE dbo.MachineObservation WHERE MachineId=@M AND StreamKeyBinary=@K AND Position=1;"
                : "UPDATE dbo.MachineObservation SET ObservationValue=N'D1C-mutated' WHERE MachineId=@M AND StreamKeyBinary=@K AND Position=1;");
            await ObserveWaitAsync(pause.Connection!, pause.Transaction!, b.ServerProcessId, mutation);
        }
        finally
        {
            pause.Release();
            try { await read.WaitAsync(TimeSpan.FromSeconds(30)); }
            finally { if (mutation is not null) await mutation!.WaitAsync(TimeSpan.FromSeconds(30)); }
        }
        Assert.Equal(AcquisitionEvidenceReadStatus.Found, (await read).Status);
        Assert.NotNull(mutation);
        Assert.Equal(1, await mutation!.WaitAsync(TimeSpan.FromSeconds(30)));
        output.WriteLine("order: exact-reader commit released mutation; subsequent exact read must detect corruption");
        Assert.Equal(AcquisitionEvidenceReadStatus.Corrupt, (await new SqlServerObservationIngestionStore(f.Connection)
            .ReadExactAsync(f.Stream, Identity(f.Repeated))).Status);
    }

    [D1CTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConflictingIdentityOrPositionInsertIsRejected(bool positionConflict)
    {
        var f = await SeedAsync();
        await using var a = new SqlConnection(f.Connection);
        await using var b = new SqlConnection(f.Connection);
        await a.OpenAsync(); await b.OpenAsync();
        await DescribeAsync(a, null, "A established source");
        Assert.NotNull(await ScalarAsync(a, null, f.Stream, "SELECT Position FROM dbo.MachineObservation WHERE MachineId=@M AND StreamKeyBinary=@K AND Position=1;"));
        await DescribeAsync(b, null, "B conflicting insert");
        await using var transaction = (SqlTransaction)await a.BeginTransactionAsync();
        // Insert a new identity/position under A and hold it uncommitted. These are
        // synthetic fixture rows, not restored GG-003 observations.
        Assert.Equal(1, await ExecuteAsync(a, transaction, f.Stream,
            "INSERT dbo.MachineObservation (MachineId,StreamKeyBinary,InstanceId,Sequence,Position,Source,Address,SignalType,ObservationValue,Quality,ObservedAt) SELECT MachineId,StreamKeyBinary,InstanceId,900,900,Source,Address,SignalType,ObservationValue,Quality,ObservedAt FROM dbo.MachineObservation WHERE MachineId=@M AND StreamKeyBinary=@K AND Position=1;"));
        var sql = positionConflict
            ? "INSERT dbo.MachineObservation (MachineId,StreamKeyBinary,InstanceId,Sequence,Position,Source,Address,SignalType,ObservationValue,Quality,ObservedAt) SELECT MachineId,StreamKeyBinary,InstanceId,901,900,Source,Address,SignalType,ObservationValue,Quality,ObservedAt FROM dbo.MachineObservation WHERE MachineId=@M AND StreamKeyBinary=@K AND Position=1;"
            : "INSERT dbo.MachineObservation (MachineId,StreamKeyBinary,InstanceId,Sequence,Position,Source,Address,SignalType,ObservationValue,Quality,ObservedAt) SELECT MachineId,StreamKeyBinary,InstanceId,900,901,Source,Address,SignalType,ObservationValue,Quality,ObservedAt FROM dbo.MachineObservation WHERE MachineId=@M AND StreamKeyBinary=@K AND Position=1;";
        var insert = ExecuteAsync(b, null, f.Stream, sql);
        SqlException failure;
        try { await ObserveWaitAsync(a, transaction, b.ServerProcessId, insert); }
        finally
        {
            await transaction.CommitAsync();
            failure = await Assert.ThrowsAsync<SqlException>(() => insert);
        }
        Assert.True(failure.Number is 2601 or 2627);
        Assert.Contains(positionConflict ? "UQ_MachineObservation_StreamPosition" : "PK_MachineObservation", failure.Message, StringComparison.Ordinal);
        output.WriteLine("order: A committed conflicting fixture key; B resumed and rejected insert: SQL {0}", failure.Number);
    }

    [D1CFact]
    public async Task ReferencedBoundaryDeletionIsRejectedByExistingForeignKey()
    {
        var f = await SeedAsync();
        await using var a = new SqlConnection(f.Connection);
        await using var b = new SqlConnection(f.Connection);
        await a.OpenAsync(); await b.OpenAsync();
        await DescribeAsync(a, null, "A authority reference");
        Assert.Equal(3m, await ScalarAsync(a, null, f.Stream, "SELECT RawAcceptedThrough FROM dbo.AcquisitionContactAuthority WHERE MachineId=@M AND StreamKeyBinary=@K;"));
        await DescribeAsync(b, null, "B endpoint deletion");
        var failure = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(b, null, f.Stream,
            "DELETE dbo.MachineObservation WHERE MachineId=@M AND StreamKeyBinary=@K AND Position=3;"));
        Assert.Equal(547, failure.Number);
        output.WriteLine("referenced boundary deletion rejected: SQL {0}", failure.Number);
    }

    [D1CFact]
    public async Task ActualSameStreamIngestionWaitsThenRejectsTheStaleCheckpoint()
    {
        var f = await SeedAsync();
        var next = AcquisitionHistoryWorkload.Batch(f.Stream, f.Repeated.Checkpoint, 1);
        using var pause = new StorePause(f.Connection, "acquisition.raw-state.write");
        var winner = Task.Run(async () => await new SqlServerObservationIngestionStore(pause.ConnectionString).CommitAsync(next, pause.Token));
        var contenderApplication = "D1C-contender:" + Guid.NewGuid().ToString("N");
        var contenderConnection = new SqlConnectionStringBuilder(f.Connection) { ApplicationName = contenderApplication }.ConnectionString;
        Task? contender = null;
        try
        {
            await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(2, await DescribeAsync(pause.Connection!, pause.Transaction, "A actual ingestion after raw writes"));
            var competing = AcquisitionHistoryWorkload.Batch(f.Stream, f.Repeated.Checkpoint, 2);
            contender = new SqlServerObservationIngestionStore(contenderConnection).CommitAsync(competing, pause.Token).AsTask();
            await ObserveWaitAsync(pause.Connection!, pause.Transaction!, null, contender, contenderApplication);
        }
        finally
        {
            pause.Release();
            try { await winner.WaitAsync(TimeSpan.FromSeconds(30)); }
            finally
            {
                if (contender is not null) await Assert.ThrowsAsync<InvalidOperationException>(() => contender);
            }
        }
        Assert.NotNull(contender);
        var restarted = new SqlServerObservationIngestionStore(f.Connection);
        Assert.Equal(next.Checkpoint, await restarted.ReadCheckpointAsync(f.Stream));
        Assert.Equal(AcquisitionEvidenceReadStatus.Found, (await restarted.ReadExactAsync(f.Stream, Identity(next))).Status);
        output.WriteLine("order: A committed; B resumed and rejected stale checkpoint; winner exact read Found");
    }

    private async Task ObserveWaitAsync(SqlConnection holder, SqlTransaction transaction, int? session, Task contender, string application = "")
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            await using var command = holder.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = 5;
            command.CommandText = """
                SELECT r.session_id,r.blocking_session_id,r.wait_type,r.wait_time,r.wait_resource,r.transaction_id,s.transaction_isolation_level
                FROM sys.dm_exec_requests r JOIN sys.dm_exec_sessions s ON s.session_id=r.session_id
                WHERE r.database_id=DB_ID() AND r.blocking_session_id=@@SPID
                  AND (r.session_id=@Session OR s.program_name=@Application);
                """;
            command.Parameters.Add("@Session", SqlDbType.Int).Value = session ?? -1;
            command.Parameters.Add("@Application", SqlDbType.NVarChar, 128).Value = application;
            await using var rows = await command.ExecuteReaderAsync(deadline.Token);
            if (await rows.ReadAsync(deadline.Token))
            {
                Assert.StartsWith("LCK_M_", rows.GetString(2), StringComparison.Ordinal);
                output.WriteLine("observed lock wait: waiter={0} blocker={1} type={2} waitMs={3} resource={4} transaction={5} isolation={6}",
                    rows[0], rows[1], rows[2], rows[3], rows[4], rows[5], rows[6]);
                return;
            }
            Assert.False(contender.IsCompleted, "Contender completed before the required SQL lock wait was observed.");
            // Yield between DMV observations; the SQL wait is the barrier, not a timed sleep.
            await Task.Yield();
        }
    }

    private async Task<int> DescribeAsync(SqlConnection connection, SqlTransaction? transaction, string phase)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandTimeout = 5;
        command.CommandText = """
            SELECT @@SPID AS SessionId,s.transaction_isolation_level,d.is_read_committed_snapshot_on,d.snapshot_isolation_state_desc,
                SERVERPROPERTY('ProductVersion') AS SqlVersion,
                (SELECT transaction_id FROM sys.dm_tran_current_transaction) AS TransactionId
            FROM sys.dm_exec_sessions s CROSS JOIN sys.databases d WHERE s.session_id=@@SPID AND d.database_id=DB_ID();
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var row = new Dictionary<string, object?>();
        for (var index = 0; index < reader.FieldCount; index++) row.Add(reader.GetName(index), reader.GetValue(index));
        output.WriteLine("{0:O} {1}: {2}; clientTransactionIsolation={3}", DateTimeOffset.UtcNow, phase, JsonSerializer.Serialize(row), transaction?.IsolationLevel);
        return Convert.ToInt32(row["transaction_isolation_level"], System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record Scenario(string Connection, ObservationStreamId Stream, ObservationIngestionBatch First, ObservationIngestionBatch Repeated);

    private static async Task<Scenario> SeedAsync()
    {
        Assert.Equal(Database, Environment.GetEnvironmentVariable(Prefix + "DISPOSABLE_DATABASE"));
        var settings = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(Prefix + "CONNECTION_STRING")
            ?? throw new InvalidOperationException("D1-C connection missing."));
        Assert.Equal(Database, settings.InitialCatalog);
        Assert.StartsWith("(localdb)\\", settings.DataSource, StringComparison.OrdinalIgnoreCase);
        Assert.True(string.IsNullOrEmpty(settings.AttachDBFilename));
        await using (var guard = new SqlConnection(settings.ConnectionString))
        {
            await guard.OpenAsync();
            await using var command = guard.CreateCommand();
            command.CommandText = """
                IF DB_NAME()<>N'GG003_ACQ_D1C_20261026' THROW 51092,'Unexpected D1-C database',1;
                SELECT COUNT_BIG(*) FROM dbo.AcquisitionEvidenceHead h JOIN dbo.AcquisitionEvidencePublication p
                  ON p.MachineId=h.MachineId AND p.StreamKeyBinary=h.StreamKeyBinary AND p.IdentityHash=h.IdentityHash
                JOIN dbo.ObservationStreamCheckpoint c ON c.MachineId=h.MachineId AND c.StreamKeyBinary=h.StreamKeyBinary
                WHERE h.MachineId='677b030b-1ede-4a67-82ae-6a494791357f' AND p.PublicationOrdinal=40545
                  AND c.InstanceId=1792061301 AND c.NextSequence=482204;
                """;
            Assert.Equal(1L, await command.ExecuteScalarAsync());
        }
        // Mutations below are confined to a new synthetic machine/stream. Restored GG-003 history is never changed.
        var stream = new ObservationStreamId(MachineId.New(), "d1c:" + Guid.NewGuid().ToString("N"));
        var store = new SqlServerObservationIngestionStore(settings.ConnectionString);
        var first = AcquisitionHistoryWorkload.Batch(stream, null, 3);
        await store.CommitAsync(first);
        var repeated = new ObservationIngestionBatch(first.Checkpoint, first.Checkpoint, [first.Observations[0]],
            first.SuccessfulContactTime.AddSeconds(1), new AcquisitionResponseEvidence(Guid.NewGuid(), 1, 1, 3,
                [new AcquisitionRecoveryEvidence(AcquisitionRecoveryReason.OutOfRange, 1, 1, 4, 1, "d1c-repeat", "v1")]));
        await store.CommitAsync(repeated);
        return new(settings.ConnectionString, stream, first, repeated);
    }

    private static string Identity(ObservationIngestionBatch batch) => Convert.ToHexString(AcquisitionEvidenceCodec.Identity(batch, AcquisitionEvidenceCodec.Encode(batch)));

    private static SqlCommand Command(SqlConnection connection, SqlTransaction? transaction, ObservationStreamId stream, string sql)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandTimeout = 25; command.CommandText = sql;
        command.Parameters.Add("@M", SqlDbType.UniqueIdentifier).Value = stream.MachineId.Value;
        command.Parameters.Add("@K", SqlDbType.VarBinary, 512).Value = OrdinalStringKeyCodec.Encode(stream.StreamKey);
        return command;
    }

    private static async Task<object?> ScalarAsync(SqlConnection connection, SqlTransaction? transaction, ObservationStreamId stream, string sql)
    { await using var command = Command(connection, transaction, stream, sql); return await command.ExecuteScalarAsync(); }
    private static async Task<int> ExecuteAsync(SqlConnection connection, SqlTransaction? transaction, ObservationStreamId stream, string sql)
    { await using var command = Command(connection, transaction, stream, sql); return await command.ExecuteNonQueryAsync(); }

    // Test-only synchronization at an existing activity's disposal, after its SQL readers
    // have closed. No production hooks, schema changes or additional source locks.
    private sealed class StorePause : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly Activity _root = new Activity("d1c-schedule").SetIdFormat(ActivityIdFormat.W3C).Start();
        private readonly ActivityListener _listener;
        private readonly IDisposable _all;
        private readonly ConcurrentBag<IDisposable> _subscriptions = [];
        private readonly ManualResetEventSlim _release = new(false);
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(60));
        private readonly string _application = "D1C-holder:" + Guid.NewGuid().ToString("N");
        private int _paused;
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string ConnectionString { get; }
        internal CancellationToken Token => _deadline.Token;
        internal SqlConnection? Connection { get; private set; }
        internal SqlTransaction? Transaction { get; private set; }

        internal StorePause(string connectionString, string stage)
        {
            ConnectionString = new SqlConnectionStringBuilder(connectionString) { ApplicationName = _application }.ConnectionString;
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "FactoryConnect.Acquisition",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    if (activity.TraceId != _root.TraceId || activity.OperationName != stage ||
                        activity.GetTagItem("stage.completed") is not true || Interlocked.CompareExchange(ref _paused, 1, 0) != 0) return;
                    Reached.TrySetResult();
                    if (!_release.Wait(TimeSpan.FromSeconds(25))) throw new TimeoutException("D1-C holder barrier was not released.");
                }
            };
            ActivitySource.AddActivityListener(_listener);
            _all = DiagnosticListener.AllListeners.Subscribe(this);
        }
        internal void Release() => _release.Set();
        public void OnNext(DiagnosticListener listener)
        { if (listener.Name == "SqlClientDiagnosticListener") _subscriptions.Add(listener.Subscribe(this)); }
        public void OnNext(KeyValuePair<string, object?> item)
        {
            if (!item.Key.EndsWith("WriteCommandBefore", StringComparison.Ordinal) ||
                item.Value?.GetType().GetProperty("Command")?.GetValue(item.Value) is not SqlCommand command || command.Connection is null ||
                new SqlConnectionStringBuilder(command.Connection.ConnectionString).ApplicationName != _application) return;
            Connection = command.Connection; Transaction = command.Transaction;
        }
        public void OnError(Exception error) => Reached.TrySetException(error);
        public void OnCompleted() { }
        public void Dispose()
        {
            Release(); _deadline.Cancel(); _all.Dispose(); foreach (var subscription in _subscriptions) subscription.Dispose();
            _listener.Dispose(); _release.Dispose(); _deadline.Dispose(); _root.Dispose();
        }
    }

    public sealed class D1CFactAttribute : FactAttribute
    {
        public D1CFactAttribute()
        { if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Prefix + "CONNECTION_STRING"))) Skip = "Opt-in fresh D1-C LocalDB clone required."; }
    }
    public sealed class D1CTheoryAttribute : TheoryAttribute
    {
        public D1CTheoryAttribute()
        { if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Prefix + "CONNECTION_STRING"))) Skip = "Opt-in fresh D1-C LocalDB clone required."; }
    }
}
