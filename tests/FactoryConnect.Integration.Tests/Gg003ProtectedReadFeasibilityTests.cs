using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace FactoryConnect.Integration.Tests;

// Diagnostic transaction composition only. Reflection invokes the actual existing
// verifier/archive helper; no production injection points or optimized verifier exist.
[Trait("Category", "SqlServerIntegration")]
[Collection(Gg003AcquisitionCloneBenchmarkDefinition.CollectionName)]
public sealed class Gg003ProtectedReadFeasibilityTests(ITestOutputHelper output)
{
    private const string Prefix = "FACTORYCONNECT_GG003_D1A1_";
    private const string Database = "GG003_ACQ_D1A1_20261026";

    [ProtectedTheory]
    [InlineData("update")]
    [InlineData("delete")]
    [InlineData("instance")]
    [InlineData("sequence")]
    [InlineData("position")]
    [InlineData("boundary")]
    public async Task ProtectedPredicatesRemainStableThroughRepeatedActualVerification(string mutation)
    {
        await RunProtectedMutationAsync(mutation, completeReadSet: true);
    }

    // Retains the original failing partial-read-set schedule. SQL 1205 still fails.
    [PartialWindowFact]
    public async Task PartialBoundaryWindowReproducesOriginalUnresolvedSchedule()
    {
        await RunProtectedMutationAsync("boundary", completeReadSet: false);
    }

    private async Task RunProtectedMutationAsync(string mutation, bool completeReadSet)
    {
        var started = DateTimeOffset.UtcNow;
        var f = await SeedAsync();
        await using var a = await OpenAsync(f.Connection);
        await using var b = await OpenAsync(f.Connection);
        await using var t = (SqlTransaction)await a.BeginTransactionAsync(IsolationLevel.Serializable);
        await DescribeAsync(a, t, "protected begin");
        (ulong Instance, ulong Sequence)[] keys = completeReadSet
            ? [(1, 1), (1, 2), (1, 3), (2, 1)] : [(1, 1), (2, 1)];
        var rows = await SourcesAsync(a, t, f.Stream, keys);
        Assert.Equal(keys.Length, rows.Count);
        output.WriteLine("schedule={0}; protected source identities={1}; complete verifier fixture needs four identities", completeReadSet ? "complete-read-set" : "original-partial-read-set", keys.Length);
        Assert.Equal(2, (await BoundariesAsync(a, t, f.Stream, [2, 4])).Count);
        var interval = Stopwatch.StartNew();
        await LocksAsync(a, t);
        var sql = mutation switch
        {
            "update" => "UPDATE dbo.MachineObservation SET ObservationValue=N'A1-mutated' WHERE MachineId=@M AND StreamKeyBinary=@K AND Position=1;",
            "delete" => "DELETE dbo.MachineObservation WHERE MachineId=@M AND StreamKeyBinary=@K AND Position=1;",
            "instance" => "UPDATE dbo.MachineObservation SET InstanceId=901 WHERE MachineId=@M AND StreamKeyBinary=@K AND Position=1;",
            "sequence" => "UPDATE dbo.MachineObservation SET Sequence=901 WHERE MachineId=@M AND StreamKeyBinary=@K AND Position=1;",
            "position" => "UPDATE dbo.MachineObservation SET Position=901 WHERE MachineId=@M AND StreamKeyBinary=@K AND Position=1;",
            "boundary" => "DELETE dbo.MachineObservation WHERE MachineId=@M AND StreamKeyBinary=@K AND Position=2;",
            _ => throw new ArgumentException("Unknown mutation", nameof(mutation))
        };
        var writerClock = Stopwatch.StartNew();
        var writer = ExecuteAsync(b, null, f.Stream, sql);
        try
        {
            await WaitAsync(a, t, b.ServerProcessId, writer);
            await LocksAsync(a, t, b.ServerProcessId);
            Assert.Equal(AcquisitionEvidenceReadStatus.Found, (await VerifyAsync(a, t, f)).Status);
            output.WriteLine("order: first actual verifier completed; retain lookup scope for next dependent verification");
            Assert.Equal(AcquisitionEvidenceReadStatus.Found, (await VerifyAsync(a, t, f)).Status);
            Assert.False(writer.IsCompleted);
        }
        catch (SqlException error) when (error.Number == 1205)
        {
            output.WriteLine("SQL 1205 remains a failed experiment. Capturing available system_health evidence before rethrow.");
            await DeadlockEvidenceAsync(f.Connection, a.ServerProcessId, b.ServerProcessId, started);
            throw;
        }
        finally
        {
            await RollbackIfActiveAsync(t);
            output.WriteLine("protected interval after lookup disposal until rollback: {0:F3} ms (bracket, not exact lock acquisition time)", interval.Elapsed.TotalMilliseconds);
            await CompleteMutationWriterAsync(writer, f.Connection, a.ServerProcessId, b.ServerProcessId, started);
        }
        output.WriteLine("writer completion latency including execution/coordination: {0:F3} ms", writerClock.Elapsed.TotalMilliseconds);
        Assert.Equal(AcquisitionEvidenceReadStatus.Corrupt, (await new SqlServerObservationIngestionStore(f.Connection)
            .ReadExactAsync(f.Stream, Identity(f.Last))).Status);
    }

    private async Task CompleteMutationWriterAsync(Task<int> writer, string connection, int holderSession, int writerSession, DateTimeOffset started)
    {
        try
        {
            Assert.Equal(1, await writer.WaitAsync(TimeSpan.FromSeconds(30)));
        }
        catch (SqlException error) when (error.Number == 1205)
        {
            await DeadlockEvidenceAsync(connection, holderSession, writerSession, started);
            throw;
        }
    }

    [ProtectedTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbsentIdentityAndBoundaryPredicatesBlockInsertUntilRollback(bool boundary)
    {
        var f = await SeedAsync();
        await using var a = await OpenAsync(f.Connection);
        await using var b = await OpenAsync(f.Connection);
        await using var t = (SqlTransaction)await a.BeginTransactionAsync(IsolationLevel.Serializable);
        await DescribeAsync(a, t, "absence probe begin");
        if (boundary) Assert.Empty(await BoundariesAsync(a, t, f.Stream, [900]));
        else Assert.Empty(await SourcesAsync(a, t, f.Stream, [(1, 900)]));
        await LocksAsync(a, t);
        var writer = ExecuteAsync(b, null, f.Stream, boundary
            ? CopyRowSql("901", "900") : CopyRowSql("900", "901"));
        try { await WaitAsync(a, t, b.ServerProcessId, writer); }
        finally { await t.RollbackAsync(); Assert.Equal(1, await writer.WaitAsync(TimeSpan.FromSeconds(30))); }
        output.WriteLine("absence stayed protected; insert completed after rollback. Predicate probe only, not a valid evidence publication.");
    }

    [ProtectedTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingIdentityAndPositionConflictsStillFailConstraints(bool position)
    {
        var f = await SeedAsync();
        await using var a = await OpenAsync(f.Connection);
        await using var b = await OpenAsync(f.Connection);
        await using var t = (SqlTransaction)await a.BeginTransactionAsync(IsolationLevel.Serializable);
        await SourcesAsync(a, t, f.Stream, [(1, 1)]);
        await BoundariesAsync(a, t, f.Stream, [1]);
        var insert = ExecuteAsync(b, null, f.Stream, position ? CopyRowSql("900", "1") : CopyRowSql("1", "900"));
        try { await WaitAsync(a, t, b.ServerProcessId, insert, completionAllowed: true); }
        finally { await t.RollbackAsync(); }
        var failure = await Assert.ThrowsAsync<SqlException>(() => insert);
        Assert.True(failure.Number is 2601 or 2627);
        output.WriteLine("constraint conflict SQL {0}; blocking is not required when an existing committed duplicate can be rejected", failure.Number);
    }

    [ProtectedTheory]
    [InlineData("valid", AcquisitionEvidenceReadStatus.Found)]
    [InlineData("value", AcquisitionEvidenceReadStatus.Corrupt)]
    [InlineData("delete", AcquisitionEvidenceReadStatus.Corrupt)]
    [InlineData("hash", AcquisitionEvidenceReadStatus.Corrupt)]
    [InlineData("version", AcquisitionEvidenceReadStatus.UnsupportedEncoding)]
    public async Task ActualVerifierClassifiesPrecommittedEvidenceEqually(string damage, AcquisitionEvidenceReadStatus expected)
    {
        var f = await SeedAsync();
        await using var a = await OpenAsync(f.Connection);
        if (damage != "valid")
            await ExecuteAsync(a, null, f.Stream, damage switch
            {
                "value" => "UPDATE dbo.MachineObservation SET ObservationValue=N'A1-mutated' WHERE MachineId=@M AND StreamKeyBinary=@K AND Position=1;",
                "delete" => "DELETE dbo.MachineObservation WHERE MachineId=@M AND StreamKeyBinary=@K AND Position=1;",
                "hash" => "UPDATE dbo.AcquisitionEvidencePublication SET ContentHash=0x01 WHERE MachineId=@M AND StreamKeyBinary=@K AND PublicationOrdinal=1;",
                "version" => "UPDATE dbo.AcquisitionEvidencePublication SET PayloadVersion=2 WHERE MachineId=@M AND StreamKeyBinary=@K AND PublicationOrdinal=1;",
                _ => throw new ArgumentException("Unknown damage", nameof(damage))
            });
        foreach (var isolation in new[] { IsolationLevel.ReadCommitted, IsolationLevel.Serializable })
        {
            await using var t = (SqlTransaction)await a.BeginTransactionAsync(isolation);
            await DescribeAsync(a, t, "matched actual verifier");
            if (isolation == IsolationLevel.Serializable)
            {
                await SourcesAsync(a, t, f.Stream, [(1, 1), (2, 1)]);
                await BoundariesAsync(a, t, f.Stream, [2, 4]);
            }
            Assert.Equal(expected, (await VerifyAsync(a, t, f)).Status);
            await t.RollbackAsync();
        }
        output.WriteLine("matched precommitted state {0}: baseline and protected candidate actual verifier returned {1}", damage, expected);
    }

    [ProtectedFact]
    public async Task ActualVerifierPropagatesCancellationAndSqlLockErrorsInBothScopes()
    {
        var f = await SeedAsync();
        await using var a = await OpenAsync(f.Connection);
        await using var b = await OpenAsync(f.Connection);
        var originalLockTimeout = await ReadSessionLockTimeoutAsync(a, null);
        foreach (var isolation in new[] { IsolationLevel.ReadCommitted, IsolationLevel.Serializable })
        {
            await using var t = (SqlTransaction)await a.BeginTransactionAsync(isolation);
            using var trace = new AcquisitionVerifierCommandTrace(output, a, t, originalLockTimeout);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => VerifyAsync(a, t, f, cancelled.Token));
            trace.ReportException(error);
            Assert.True(trace.AllCommandsOwned);
            await t.RollbackAsync();
        }
        await using var writerTransaction = (SqlTransaction)await b.BeginTransactionAsync();
        await ExecuteAsync(b, writerTransaction, f.Stream,
            "UPDATE dbo.MachineObservation SET ObservationValue=N'A1-uncommitted' WHERE MachineId=@M AND StreamKeyBinary=@K AND Position=1;");
        try
        {
            foreach (var isolation in new[] { IsolationLevel.ReadCommitted, IsolationLevel.Serializable })
            {
                await using var t = (SqlTransaction)await a.BeginTransactionAsync(isolation);
                await SetSessionLockTimeoutAsync(a, t, 0);
                var effectiveTimeout = await ReadSessionLockTimeoutAsync(a, t);
                Assert.Equal(0, effectiveTimeout);
                output.WriteLine("session={0}; isolation={1}; @@LOCK_TIMEOUT={2}; verifier command timeout remains unchanged", a.ServerProcessId, isolation, effectiveTimeout);
                using var trace = new AcquisitionVerifierCommandTrace(output, a, t, effectiveTimeout);
                using var sampling = new CancellationTokenSource();
                // B has no active reader; its source UPDATE completed before this task.
                // The sampler never issues SQL on verifier connection A.
                var samples = SampleVerifierRequestAsync(b, writerTransaction, a.ServerProcessId, sampling.Token);
                SqlException error;
                bool samplingHealthy;
                try
                {
                    error = await Assert.ThrowsAsync<SqlException>(() => VerifyAsync(a, t, f));
                    trace.ReportException(error); // Emit original SQL stack before the number assertion.
                }
                catch (Exception wrapperError)
                {
                    trace.ReportException(wrapperError, "test-wrapper");
                    throw;
                }
                finally
                {
                    sampling.Cancel();
                    samplingHealthy = await samples;
                }
                Assert.True(trace.CommandsStarted > 0, "Verifier command trace must be active in the executing binary.");
                Assert.True(trace.AllCommandsOwned);
                Assert.Equal(1222, error.Number);
                Assert.True(samplingHealthy, "Writer-side request sampling failed; inspect VERIFIER_SAMPLE_ERROR.");
                output.WriteLine("{0}: actual verifier SQL lock error {1}; no corruption translation", isolation, error.Number);
                await t.RollbackAsync();
            }
        }
        finally
        {
            await writerTransaction.RollbackAsync();
            await SetSessionLockTimeoutAsync(a, null, originalLockTimeout);
            Assert.Equal(originalLockTimeout, await ReadSessionLockTimeoutAsync(a, null));
        }
    }

    private async Task<bool> SampleVerifierRequestAsync(SqlConnection observer, SqlTransaction transaction, int verifierSession, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        var count = 0;
        var activeRequests = 0;
        var observerSession = observer.ServerProcessId;
        string? previous = null;
        try
        {
            do
            {
                await using var command = observer.CreateCommand(); command.Transaction = transaction; command.CommandTimeout = 3;
                command.CommandText = """
                    SELECT s.session_id,s.lock_timeout,s.transaction_isolation_level,r.blocking_session_id,r.status,r.command,r.wait_type,r.wait_time,
                        r.wait_resource,r.total_elapsed_time,r.cpu_time,r.reads,r.logical_reads,r.granted_query_memory,r.transaction_id
                    FROM sys.dm_exec_sessions s LEFT JOIN sys.dm_exec_requests r ON r.session_id=s.session_id WHERE s.session_id=@Verifier;
                    """;
                command.Parameters.Add("@Verifier", SqlDbType.Int).Value = verifierSession;
                await using (var reader = await command.ExecuteReaderAsync(token))
                {
                    if (await reader.ReadAsync(token))
                    {
                        var row = new Dictionary<string, object?>();
                        for (var index = 0; index < reader.FieldCount; index++) row.Add(reader.GetName(index), reader.IsDBNull(index) ? null : reader.GetValue(index));
                        var state = JsonSerializer.Serialize(row); count++;
                        if (row["command"] is not null) activeRequests++;
                        if (state != previous) output.WriteLine("VERIFIER_REQUEST_SAMPLE {0:O} observerSession={1} {2}", DateTimeOffset.UtcNow, observerSession, state);
                        previous = state;
                    }
                }
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (SqlException error)
        {
            output.WriteLine("VERIFIER_SAMPLE_ERROR sqlNumber={0} {1}", error.Number, error.ToString());
            return false;
        }
        finally { output.WriteLine("VERIFIER_SAMPLES_END sessionSamples={0} activeRequestSamples={1}; missing request samples do not establish absence of an instantaneous lock conflict", count, activeRequests); }
        return true;
    }

    [ProtectedTheory]
    [InlineData("rollback")]
    [InlineData("cancel")]
    [InlineData("verification-failure")]
    public async Task ProtectedArchiveHelperLeavesNoPartialStateWhenTransactionAborts(string outcome)
    {
        var f = await SeedAsync();
        await using var a = await OpenAsync(f.Connection);
        var before = await StateAsync(a, f.Stream);
        await using (var t = (SqlTransaction)await a.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            await SourcesAsync(a, t, f.Stream, [(1, 1), (2, 1)]);
            var batch = Continuation(f.Last);
            await StageAsync(a, t, batch);
            if (outcome == "verification-failure")
            {
                await ExecuteAsync(a, t, f.Stream, "UPDATE dbo.MachineObservation SET ObservationValue=N'A1-mutated' WHERE MachineId=@M AND StreamKeyBinary=@K AND Position=1;");
                await Assert.ThrowsAsync<InvalidDataException>(() => AppendAsync(a, t, batch));
            }
            else
            {
                await AppendAsync(a, t, batch);
                if (outcome == "cancel")
                {
                    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvokeAsync<AcquisitionEvidenceReadResult>("ReadEvidenceAsync", a, t, f.Stream, Convert.FromHexString(Identity(batch)), cancelled.Token));
                }
            }
            await t.RollbackAsync();
        }
        Assert.Equal(before, await StateAsync(a, f.Stream));
        output.WriteLine("{0}: actual archive helper + test-owned staging rolled back all five structures; not a replacement CommitAsync implementation", outcome);
    }

    [ProtectedFact]
    public async Task MatchedSyntheticAppendReplayAndReaderLatencyAreReportedSeparately()
    {
        foreach (var isolation in new[] { IsolationLevel.ReadCommitted, IsolationLevel.Serializable })
        {
            var f = await SeedAsync();
            using var capture = new AcquisitionPersistenceCapture(f.Connection);
            await using var a = await OpenAsync(capture.ConnectionString);
            var clock = Stopwatch.StartNew();
            var batch = Continuation(f.Last);
            await using (var t = (SqlTransaction)await a.BeginTransactionAsync(isolation))
            {
                if (isolation == IsolationLevel.Serializable)
                {
                    await SourcesAsync(a, t, f.Stream, [(1, 1), (2, 1)]);
                    await BoundariesAsync(a, t, f.Stream, [2, 4]);
                }
                await StageAsync(a, t, batch);
                await AppendAsync(a, t, batch);
                await t.CommitAsync();
            }
            output.WriteLine("{0}: synthetic archive append transaction composition {1:F3} ms; source commands={2}", isolation, clock.Elapsed.TotalMilliseconds, capture.SourceCommands);
            var beforeReplay = await StateAsync(a, f.Stream);
            var startCommands = capture.SourceCommands; clock.Restart();
            await using (var t = (SqlTransaction)await a.BeginTransactionAsync(isolation))
            {
                Assert.True(await InvokeAsync<bool>("ValidateEvidenceReplayAsync", a, t, f.Stream,
                    AcquisitionEvidenceCodec.Identity(batch, AcquisitionEvidenceCodec.Encode(batch)), AcquisitionEvidenceCodec.Encode(batch), CancellationToken.None));
                await t.CommitAsync();
            }
            output.WriteLine("{0}: actual replay verifier {1:F3} ms; source commands={2}", isolation, clock.Elapsed.TotalMilliseconds, capture.SourceCommands - startCommands);
            Assert.Equal(beforeReplay, await StateAsync(a, f.Stream));
            Assert.Equal(batch.Checkpoint, await new SqlServerObservationIngestionStore(f.Connection).ReadCheckpointAsync(f.Stream));
            Assert.Null(capture.Error);
        }
    }

    [ProtectedTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObservationReaderContentionDuringStagedArchiveAppendIsMeasured(bool protect)
    {
        var f = await SeedAsync();
        await using var a = await OpenAsync(f.Connection);
        await using var t = (SqlTransaction)await a.BeginTransactionAsync(protect ? IsolationLevel.Serializable : IsolationLevel.ReadCommitted);
        if (protect)
        {
            await SourcesAsync(a, t, f.Stream, [(1, 1), (2, 1)]);
            await BoundariesAsync(a, t, f.Stream, [2, 4]);
        }
        var batch = Continuation(f.Last);
        await StageAsync(a, t, batch);
        await AppendAsync(a, t, batch);
        var application = "D1A1-reader:" + Guid.NewGuid().ToString("N");
        var connection = new SqlConnectionStringBuilder(f.Connection) { ApplicationName = application }.ConnectionString;
        var timer = Stopwatch.StartNew();
        var read = new SqlServerObservationIngestionStore(connection).ReadAsync(new ObservationReadRequest(f.Stream, new ObservationPosition(4), 1)).AsTask();
        try { await WaitAsync(a, t, null, read, application); await t.CommitAsync(); }
        catch { await t.RollbackAsync(); throw; }
        finally { await read.WaitAsync(TimeSpan.FromSeconds(30)); }
        Assert.Equal(batch.Observations[0].Observation, Assert.Single((await read).Observations).Observation);
        output.WriteLine("protect={0}: actual observation-reader latency {1:F3} ms including coordination; not HTTP reporting latency", protect, timer.Elapsed.TotalMilliseconds);
    }

    [ProtectedFact]
    public async Task MaximumKeyShapesProducePlansWithoutLosingCrossInstanceIdentity()
    {
        var f = await SeedAsync();
        await using var a = await OpenAsync(f.Connection);
        await using var t = (SqlTransaction)await a.BeginTransactionAsync(IsolationLevel.Serializable);
        var keys = Enumerable.Range(1, 128).Select(index => (1UL, (ulong)index)).ToArray();
        Assert.Equal(3, (await SourcesAsync(a, t, f.Stream, keys)).Count);
        Assert.Equal(4, (await BoundariesAsync(a, t, f.Stream, Enumerable.Range(1, 128).Select(index => (ulong)index).ToArray())).Count);
        var collision = await SourcesAsync(a, t, f.Stream, [(1, 1), (2, 1)]);
        Assert.Equal(2, collision.Count);
        Assert.NotEqual(collision[0][6], collision[1][6]);
        await t.RollbackAsync();
    }

    // Intended bounded cross-publication lookup shapes. No hints or forced indexes.
    // Raw columns are retained; the actual verifier performs semantic materialization.
    private async Task<List<object[]>> SourcesAsync(SqlConnection c, SqlTransaction t, ObservationStreamId stream, (ulong Instance, ulong Sequence)[] keys)
    {
        Assert.InRange(keys.Length, 1, 128);
        Assert.Equal(keys.Length, keys.Distinct().Count());
        await using var command = Command(c, t, stream, "");
        var predicates = new List<string>();
        for (var index = 0; index < keys.Length; index++)
        {
            var suffix = index.ToString(CultureInfo.InvariantCulture);
            predicates.Add("(InstanceId=@I" + suffix + " AND Sequence=@S" + suffix + ")");
            command.Parameters.Add(SqlServerUInt64.CreateParameter("@I" + suffix, keys[index].Instance));
            command.Parameters.Add(SqlServerUInt64.CreateParameter("@S" + suffix, keys[index].Sequence));
        }
        command.CommandText = "SELECT TOP (@Limit) InstanceId,Sequence,Position,Source,Address,SignalType,ObservationValue,Quality,ObservedAt FROM dbo.MachineObservation WHERE MachineId=@M AND StreamKeyBinary=@K AND (" + string.Join(" OR ", predicates) + ");";
        var rows = await PlannedRowsAsync(command, keys.Length);
        Assert.Equal(rows.Count, rows.Select(row => (row[0], row[1])).Distinct().Count());
        return rows;
    }

    private async Task<List<object[]>> BoundariesAsync(SqlConnection c, SqlTransaction t, ObservationStreamId stream, ulong[] positions)
    {
        Assert.InRange(positions.Length, 1, 128);
        Assert.Equal(positions.Length, positions.Distinct().Count());
        await using var command = Command(c, t, stream, "");
        var names = new List<string>();
        for (var index = 0; index < positions.Length; index++)
        {
            var name = "@P" + index.ToString(CultureInfo.InvariantCulture); names.Add(name);
            command.Parameters.Add(SqlServerUInt64.CreateParameter(name, positions[index]));
        }
        command.CommandText = "SELECT TOP (@Limit) Position FROM dbo.MachineObservation WHERE MachineId=@M AND StreamKeyBinary=@K AND Position IN (" + string.Join(",", names) + ");";
        var rows = await PlannedRowsAsync(command, positions.Length);
        Assert.Equal(rows.Count, rows.Select(row => row[0]).Distinct().Count());
        return rows;
    }

    private async Task<List<object[]>> PlannedRowsAsync(SqlCommand command, int limit)
    {
        command.Parameters.Add("@Limit", SqlDbType.Int).Value = limit + 1;
        command.CommandText = "SET STATISTICS XML ON;" + command.CommandText + "SET STATISTICS XML OFF;";
        var rows = new List<object[]>(); var plans = 0;
        try
        {
            await using var reader = await command.ExecuteReaderAsync();
            do
            {
                var plan = reader.FieldCount == 1 && (reader.GetDataTypeName(0) == "xml" || reader.GetName(0).Contains("Showplan", StringComparison.OrdinalIgnoreCase));
                while (await reader.ReadAsync())
                {
                    if (plan)
                    {
                        var xml = reader.GetDataTypeName(0) == "xml" ? reader.GetSqlXml(0).Value : reader.GetString(0); plans++;
                        var document = XDocument.Parse(xml);
                        var indexes = document.Descendants().Where(element => element.Name.LocalName == "Object")
                            .Select(element => (string?)element.Attribute("Index")).Where(index => index is not null).Distinct();
                        output.WriteLine("actual plan indexes: {0}", string.Join(",", indexes));
                        output.WriteLine("ACTUAL_PLAN_XML {0}", xml);
                    }
                    else
                    {
                        var row = new object[reader.FieldCount]; reader.GetValues(row); rows.Add(row);
                        if (rows.Count > limit) throw new InvalidDataException("Lookup returned excess rows; no silent truncation.");
                    }
                }
            } while (await reader.NextResultAsync());
        }
        finally
        {
            await using var reset = command.Connection!.CreateCommand(); reset.Transaction = command.Transaction;
            reset.CommandText = "SET STATISTICS XML OFF;"; reset.CommandTimeout = 5; await reset.ExecuteNonQueryAsync();
        }
        Assert.True(plans > 0, "Actual execution plan result is required.");
        output.WriteLine("probe keys={0}; rows={1}; parameters={2}; SQL executions=1 (+statistics reset)", limit, rows.Count, command.Parameters.Count);
        return rows;
    }

    private async Task<bool> WaitAsync(SqlConnection a, SqlTransaction t, int? waiter, Task task, string application = "", bool completionAllowed = false)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            bound.Token.ThrowIfCancellationRequested();
            await using var command = Command(a, t, null, "SELECT r.blocking_session_id,r.wait_type,r.wait_time,r.wait_resource,r.transaction_id FROM sys.dm_exec_requests r JOIN sys.dm_exec_sessions s ON s.session_id=r.session_id WHERE (r.session_id=@Waiter OR s.program_name=@Application) AND r.blocking_session_id=@@SPID;");
            command.Parameters.Add("@Waiter", SqlDbType.Int).Value = waiter ?? -1;
            command.Parameters.Add("@Application", SqlDbType.NVarChar, 128).Value = application;
            await using var reader = await command.ExecuteReaderAsync(bound.Token);
            if (await reader.ReadAsync(bound.Token))
            {
                Assert.StartsWith("LCK_M_", reader.GetString(1), StringComparison.Ordinal);
                output.WriteLine("{0:O} waiter={1} blocker={2} wait={3} sampledMs={4} resource={5} waiterTransaction={6}", DateTimeOffset.UtcNow, waiter is null ? (object)application : waiter.Value, reader[0], reader[1], reader[2], reader[3], reader[4]);
                return true;
            }
            if (completionAllowed && task.IsCompleted) return false;
            Assert.False(task.IsCompleted, "Required predicate wait was not observed.");
            await Task.Yield();
        }
    }

    private async Task DescribeAsync(SqlConnection c, SqlTransaction t, string phase)
    {
        await using var command = Command(c, t, null, "SELECT @@SPID,s.transaction_isolation_level,d.is_read_committed_snapshot_on,d.snapshot_isolation_state_desc,(SELECT transaction_id FROM sys.dm_tran_current_transaction),SERVERPROPERTY('ProductVersion') FROM sys.dm_exec_sessions s JOIN sys.databases d ON d.database_id=DB_ID() WHERE s.session_id=@@SPID;");
        await using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
        var values = new object[reader.FieldCount]; reader.GetValues(values);
        output.WriteLine("{0:O} {1} session/isolation/RCSI/snapshot/transaction/version={2}", DateTimeOffset.UtcNow, phase, JsonSerializer.Serialize(values));
    }

    private async Task LocksAsync(SqlConnection c, SqlTransaction t, int? otherSession = null)
    {
        await using var command = Command(c, t, null, "SELECT request_session_id,resource_type,request_mode,request_status,resource_associated_entity_id,resource_description FROM sys.dm_tran_locks WHERE resource_database_id=DB_ID() AND (request_session_id=@@SPID OR request_session_id=@Other);");
        command.Parameters.Add("@Other", SqlDbType.Int).Value = otherSession ?? -1;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) { var values = new object[reader.FieldCount]; reader.GetValues(values); output.WriteLine("LOCK {0}", JsonSerializer.Serialize(values)); }
        await reader.DisposeAsync();
        await using var map = Command(c, t, null, "SELECT p.hobt_id,i.name FROM sys.partitions p JOIN sys.indexes i ON i.object_id=p.object_id AND i.index_id=p.index_id WHERE p.object_id=OBJECT_ID(N'dbo.MachineObservation');");
        await using var indexes = await map.ExecuteReaderAsync();
        while (await indexes.ReadAsync()) output.WriteLine("INDEX_HOBT {0} {1}", indexes[0], indexes[1]);
    }

    private async Task DeadlockEvidenceAsync(string connection, int holder, int writer, DateTimeOffset started)
    {
        try
        {
            await using var c = await OpenAsync(connection);
            await using var command = Command(c, null, null, "SELECT DB_ID(),CONVERT(nvarchar(max),t.target_data) FROM sys.dm_xe_sessions s JOIN sys.dm_xe_session_targets t ON t.event_session_address=s.address WHERE s.name=N'system_health' AND t.target_name=N'ring_buffer';");
            command.CommandTimeout = 5;
            await using var reader = await command.ExecuteReaderAsync();
            var count = 0;
            while (await reader.ReadAsync())
            {
                var database = reader.GetInt32(0).ToString(CultureInfo.InvariantCulture);
                var xml = XDocument.Parse(reader.GetString(1));
                foreach (var entry in xml.Descendants("event").Where(element => (string?)element.Attribute("name") == "xml_deadlock_report"))
                {
                    if (!DateTimeOffset.TryParse((string?)entry.Attribute("timestamp"), CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal, out var timestamp) || timestamp < started) continue;
                    var graph = entry.Descendants("deadlock").FirstOrDefault();
                    if (graph is null || !graph.Descendants().Attributes("dbid").Any(attribute => attribute.Value == database)) continue;
                    var sessions = graph.Descendants("process").Select(element => (string?)element.Attribute("spid")).ToArray();
                    if (!sessions.Contains(holder.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal) ||
                        !sessions.Contains(writer.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal)) continue;
                    output.WriteLine("MATCHING_DEADLOCK_XML {0}", graph.ToString(SaveOptions.DisableFormatting)); count++;
                }
            }
            output.WriteLine("matching system_health deadlock graphs={0}; missing/asynchronous/truncated evidence does not disprove a deadlock", count);
        }
        catch (Exception error) when (error is SqlException or System.Xml.XmlException or InvalidOperationException)
        { output.WriteLine("deadlock evidence unavailable: {0}; original SQL failure remains authoritative", error.GetType().Name); }
    }

    private static async Task<int> ReadSessionLockTimeoutAsync(SqlConnection c, SqlTransaction? t)
    {
        await using var command = c.CreateCommand(); command.Transaction = t;
        command.CommandText = "SELECT @@LOCK_TIMEOUT;"; command.CommandTimeout = 5;
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task SetSessionLockTimeoutAsync(SqlConnection c, SqlTransaction? t, int milliseconds)
    {
        await using var command = c.CreateCommand(); command.Transaction = t;
        command.CommandText = "SET LOCK_TIMEOUT " + milliseconds.ToString(CultureInfo.InvariantCulture) + ";";
        command.CommandTimeout = 5;
        Assert.Empty(command.Parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RollbackIfActiveAsync(SqlTransaction transaction)
    {
        if (transaction.Connection is not null) await transaction.RollbackAsync();
    }

    private sealed record Fixture(string Connection, ObservationStreamId Stream, ObservationIngestionBatch Last);
    private static async Task<Fixture> SeedAsync()
    {
        Assert.Equal(Database, Environment.GetEnvironmentVariable(Prefix + "DISPOSABLE_DATABASE"));
        var settings = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(Prefix + "CONNECTION_STRING") ?? throw new InvalidOperationException("D1-A1 connection required."));
        Assert.Equal(Database, settings.InitialCatalog);
        Assert.StartsWith("(localdb)\\", settings.DataSource, StringComparison.OrdinalIgnoreCase);
        Assert.True(string.IsNullOrEmpty(settings.AttachDBFilename));
        await using (var guard = await OpenAsync(settings.ConnectionString))
        {
            await using var command = Command(guard, null, null, "IF DB_NAME()<>N'GG003_ACQ_D1A1_20261026' THROW 51093,'Unexpected D1-A1 database',1; SELECT COUNT_BIG(*) FROM dbo.AcquisitionEvidenceHead h JOIN dbo.AcquisitionEvidencePublication p ON p.MachineId=h.MachineId AND p.StreamKeyBinary=h.StreamKeyBinary AND p.IdentityHash=h.IdentityHash JOIN dbo.ObservationStreamCheckpoint c ON c.MachineId=h.MachineId AND c.StreamKeyBinary=h.StreamKeyBinary WHERE h.MachineId='677b030b-1ede-4a67-82ae-6a494791357f' AND p.PublicationOrdinal=40545 AND c.InstanceId=1792061301 AND c.NextSequence=482204;");
            Assert.Equal(1L, await command.ExecuteScalarAsync());
        }
        var stream = new ObservationStreamId(MachineId.New(), "d1a1:" + Guid.NewGuid().ToString("N"));
        var store = new SqlServerObservationIngestionStore(settings.ConnectionString);
        var first = AcquisitionHistoryWorkload.Batch(stream, null, 2); await store.CommitAsync(first);
        var second = AcquisitionHistoryWorkload.Batch(stream, first.Checkpoint, 1); await store.CommitAsync(second);
        var repeated = new ObservationIngestionBatch(second.Checkpoint, second.Checkpoint, [first.Observations[0]], second.SuccessfulContactTime.AddSeconds(1),
            new AcquisitionResponseEvidence(Guid.NewGuid(), 1, 1, 3, [new AcquisitionRecoveryEvidence(AcquisitionRecoveryReason.OutOfRange, 1, 1, 4, 1, "d1a1-repeat", "v1")]));
        await store.CommitAsync(repeated);
        var time = repeated.SuccessfulContactTime.AddSeconds(1);
        var reset = new ObservationIngestionBatch(repeated.Checkpoint, new ObservationCheckpoint(stream, 2, 2),
            [new SequencedMachineObservation(1, first.Observations[0].Observation with { Value = "INSTANCE2", Timestamp = time })], time,
            new AcquisitionResponseEvidence(Guid.NewGuid(), 1, 1, 1, [new AcquisitionRecoveryEvidence(AcquisitionRecoveryReason.InstanceChanged, 1, 2, 4, 1, "d1a1-instance", "v1")]));
        await store.CommitAsync(reset);
        return new(settings.ConnectionString, stream, reset);
    }

    private static ObservationIngestionBatch Continuation(ObservationIngestionBatch last)
    {
        var time = last.SuccessfulContactTime.AddSeconds(1);
        return new(last.Checkpoint, new ObservationCheckpoint(last.Checkpoint.StreamId, 2, 3),
            [new SequencedMachineObservation(2, last.Observations[0].Observation with { Timestamp = time })], time,
            new AcquisitionResponseEvidence(Guid.NewGuid(), 2, 2, 2, []));
    }

    private static async Task StageAsync(SqlConnection c, SqlTransaction t, ObservationIngestionBatch batch)
    {
        await using var command = Command(c, t, batch.Checkpoint.StreamId, "UPDATE dbo.ObservationStreamCheckpoint SET NextSequence=3 WHERE MachineId=@M AND StreamKeyBinary=@K; INSERT dbo.MachineObservation (MachineId,StreamKeyBinary,InstanceId,Sequence,Position,Source,Address,SignalType,ObservationValue,Quality,ObservedAt) VALUES (@M,@K,2,2,5,@Source,@Address,@Type,@Value,@Quality,@Time); UPDATE dbo.AcquisitionContactAuthority SET SuccessfulContactTime=@Time,RawAcceptedThrough=5,AcquisitionRevision=AcquisitionRevision+1 WHERE MachineId=@M AND StreamKeyBinary=@K;");
        var observation = batch.Observations[0].Observation;
        command.Parameters.Add("@Source", SqlDbType.NVarChar, 256).Value = observation.Source;
        command.Parameters.Add("@Address", SqlDbType.NVarChar, 512).Value = observation.Address;
        command.Parameters.Add("@Type", SqlDbType.TinyInt).Value = (byte)observation.Type;
        command.Parameters.Add("@Value", SqlDbType.NVarChar, -1).Value = observation.Value!;
        command.Parameters.Add("@Quality", SqlDbType.TinyInt).Value = (byte)observation.Quality;
        command.Parameters.Add("@Time", SqlDbType.DateTimeOffset).Value = observation.Timestamp;
        await command.ExecuteNonQueryAsync();
    }

    private static Task AppendAsync(SqlConnection c, SqlTransaction t, ObservationIngestionBatch batch) =>
        (Task)Method("AppendEvidenceAsync").Invoke(null, [c, t, batch, AcquisitionEvidenceCodec.Identity(batch, AcquisitionEvidenceCodec.Encode(batch)),
            AcquisitionEvidenceCodec.Encode(batch), new ObservationPosition(4), new ObservationPosition(5), CancellationToken.None])!;
    private static Task<AcquisitionEvidenceReadResult> VerifyAsync(SqlConnection c, SqlTransaction t, Fixture f, CancellationToken token = default) =>
        InvokeAsync<AcquisitionEvidenceReadResult>("ReadEvidenceAsync", c, t, f.Stream, Convert.FromHexString(Identity(f.Last)), token);
    private static Task<T> InvokeAsync<T>(string name, params object?[] arguments) => (Task<T>)Method(name).Invoke(null, arguments)!;
    private static MethodInfo Method(string name) => typeof(SqlServerObservationIngestionStore).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(name);
    private static string Identity(ObservationIngestionBatch batch) => AcquisitionPublicationIdentity.Create(batch.Checkpoint.StreamId, batch.AcquisitionEvidence!.PublicationId);
    private static string CopyRowSql(string sequence, string position) => "INSERT dbo.MachineObservation (MachineId,StreamKeyBinary,InstanceId,Sequence,Position,Source,Address,SignalType,ObservationValue,Quality,ObservedAt) SELECT MachineId,StreamKeyBinary,InstanceId," + sequence + "," + position + ",Source,Address,SignalType,ObservationValue,Quality,ObservedAt FROM dbo.MachineObservation WHERE MachineId=@M AND StreamKeyBinary=@K AND Position=1;";
    private static async Task<SqlConnection> OpenAsync(string connection)
    { var c = new SqlConnection(connection); try { await c.OpenAsync(); return c; } catch { await c.DisposeAsync(); throw; } }
    private static SqlCommand Command(SqlConnection c, SqlTransaction? t, ObservationStreamId? stream, string sql)
    {
        var command = c.CreateCommand(); command.Transaction = t; command.CommandText = sql; command.CommandTimeout = 15;
        if (stream is not null)
        {
            command.Parameters.Add("@M", SqlDbType.UniqueIdentifier).Value = stream.MachineId.Value;
            command.Parameters.Add("@K", SqlDbType.VarBinary, 512).Value = OrdinalStringKeyCodec.Encode(stream.StreamKey);
        }
        return command;
    }
    private static async Task<int> ExecuteAsync(SqlConnection c, SqlTransaction? t, ObservationStreamId stream, string sql)
    { await using var command = Command(c, t, stream, sql); return await command.ExecuteNonQueryAsync(); }
    private static async Task<string> StateAsync(SqlConnection c, ObservationStreamId stream)
    {
        await using var command = Command(c, null, stream, "SELECT (SELECT * FROM dbo.ObservationStreamCheckpoint WHERE MachineId=@M AND StreamKeyBinary=@K FOR JSON PATH,INCLUDE_NULL_VALUES),(SELECT * FROM dbo.MachineObservation WHERE MachineId=@M AND StreamKeyBinary=@K ORDER BY Position FOR JSON PATH,INCLUDE_NULL_VALUES),(SELECT * FROM dbo.AcquisitionContactAuthority WHERE MachineId=@M AND StreamKeyBinary=@K FOR JSON PATH,INCLUDE_NULL_VALUES),(SELECT * FROM dbo.AcquisitionEvidencePublication WHERE MachineId=@M AND StreamKeyBinary=@K ORDER BY PublicationOrdinal FOR JSON PATH,INCLUDE_NULL_VALUES),(SELECT * FROM dbo.AcquisitionEvidenceHead WHERE MachineId=@M AND StreamKeyBinary=@K FOR JSON PATH,INCLUDE_NULL_VALUES);");
        await using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
        var values = new object[reader.FieldCount]; reader.GetValues(values); return JsonSerializer.Serialize(values);
    }
    public sealed class PartialWindowFactAttribute : FactAttribute
    {
        public PartialWindowFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Prefix + "CONNECTION_STRING")) ||
                Environment.GetEnvironmentVariable(Prefix + "REPRODUCE_PARTIAL_WINDOW") != "1")
                Skip = "Original unresolved partial-window schedule; requires explicit REPRODUCE_PARTIAL_WINDOW=1 on disposable clone.";
        }
    }
    public sealed class ProtectedFactAttribute : FactAttribute
    { public ProtectedFactAttribute() { if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Prefix + "CONNECTION_STRING"))) Skip = "Opt-in fresh D1-A1 clone required."; } }
    public sealed class ProtectedTheoryAttribute : TheoryAttribute
    { public ProtectedTheoryAttribute() { if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Prefix + "CONNECTION_STRING"))) Skip = "Opt-in fresh D1-A1 clone required."; } }
}
