using System.Data;
using System.Diagnostics;
using System.Text.Json;
using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace FactoryConnect.Integration.Tests;

// Supporting source-path measurements ONLY. These are not C1-C8 V2 acceptance tests.
[Trait("Category", "SqlServerIntegration")]
public sealed class B1SourceWriterLockCharacterizationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("aggregation-r0")]
    [InlineData("transition-begin")]
    [InlineData("transition-complete")]
    [InlineData("outcome-admission")]
    [InlineData("legacy-outcome")]
    public async Task ActualWriterWaitsAtObservedSqlBarrierThenCompletes(string path)
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var observer = new SqlConnection(database.ConnectionString);
        await observer.OpenAsync();
        await SqlServerMigrationEngine.CreateDefault().ApplyAsync(observer, TimeSpan.FromMinutes(2), CancellationToken.None);
        var machine = new MachineId(Guid.NewGuid());
        var processor = new MetricAggregationProcessorId("b1-source-lock-probe");
        var site = new SiteId("B1-SITE");
        var shift = new ShiftId("B1-SHIFT");
        var schedule = new ShiftScheduleAssignmentId("B1-SCHEDULE");
        var start = new DateTimeOffset(2026, 10, 16, 6, 0, 0, TimeSpan.Zero);
        var occurrence = new ShiftOccurrenceId(site, schedule, shift, start, start.AddHours(8));
        var day = new ProductionDayId(site, new DateOnly(2026, 10, 16));
        var inputs = new SqlServerMetricInputStore(database.ConnectionString);
        var first = await inputs.AppendAsync(new DurableMetricInputAppend(MetricInputStreamId.ForMachine(machine),
            new DurableMetricInputFact
            {
                Id = new MetricInputFactId("b1-quantity-1"), Key = "quantity.part-count-increment", Value = 2m,
                Unit = "count", StartsAtUtc = start, EndsAtUtc = start.AddSeconds(1),
                CompanyId = new CompanyId("B1-COMPANY"), SiteId = site, MachineId = machine,
                ShiftId = shift, ShiftScheduleAssignmentId = schedule,
                PartId = new PartId("B1-PART"), OperationId = new OperationId("B1-OP"),
                SourceQuantityEvidenceId = new ProductionQuantityEvidenceId("b1-source-1"),
            }, occurrence, day), CancellationToken.None);
        var aggregation = new SqlServerMetricAggregationStore(database.ConnectionString);
        var revision = new MetricAggregationCheckpoint(processor, first.StreamId, first.Position);
        await aggregation.CommitAsync(new MetricAggregationCommit(processor, null, revision, [first]), CancellationToken.None);
        var standards = await new SqlServerProductionStandardAuthority(database.ConnectionString).ReadCurrentCutAsync();
        var sources = await aggregation.ReadProductionQuantityTransitionSourcesAsync(revision, machine, null, CancellationToken.None);
        var source = Assert.Single(sources);
        var resolution = ProductionStandardResolver.Resolve(source.Evidence, source.ShiftOccurrenceId, source.ProductionDayId, standards);
        var proposed = new PublishedProductionReferenceTimeOutcome(new ProductionReferenceTimeAuthorityRevision(1), resolution);
        var transitions = new SqlServerProductionReferenceTimePublicationTransitionStore(database.ConnectionString);
        var admissions = new SqlServerProductionReferenceTimeTransitionOutcomeStore(database.ConnectionString);
        if (path is "transition-complete" or "outcome-admission")
            await transitions.BeginAsync(processor, revision.Position, null, new ProductionReferenceTimeAuthorityRevision(0), standards.Revision);
        if (path == "transition-complete")
            await admissions.AdmitAsync(processor, revision.Position, proposed);

        PositionedMetricInputFact? second = null;
        MetricAggregationCheckpoint? next = null;
        if (path == "aggregation-r0")
        {
            second = await inputs.AppendAsync(new DurableMetricInputAppend(first.StreamId,
                first.Fact with { Id = new MetricInputFactId("b1-quantity-2"), SourceQuantityEvidenceId = new ProductionQuantityEvidenceId("b1-source-2") },
                occurrence, day), CancellationToken.None);
            next = new MetricAggregationCheckpoint(processor, first.StreamId, second.Position);
        }

        // The blocker is a separate, explicitly rolled-back fixture transaction.
        // Its lock is a deterministic barrier, not an emulation of a V2 validator.
        await using var blocker = new SqlConnection(database.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = blocker.BeginTransaction(IsolationLevel.Serializable);
        var table = path switch
        {
            "aggregation-r0" => "ProductionReferenceTimeRevision",
            "legacy-outcome" => "MetricAggregationRevision",
            _ => "MetricAggregationContribution",
        };
        await using (var command = blocker.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"SELECT COUNT_BIG(*) FROM dbo.{table} WITH (TABLOCKX,HOLDLOCK);";
            await command.ExecuteScalarAsync();
        }
        var blockerSpid = blocker.ServerProcessId;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        async Task Write()
        {
            switch (path)
            {
                case "aggregation-r0":
                    await aggregation.CommitAsync(new MetricAggregationCommit(processor, revision, next!, [second!]), timeout.Token);
                    break;
                case "transition-begin":
                    await transitions.BeginAsync(processor, revision.Position, null, new ProductionReferenceTimeAuthorityRevision(0), standards.Revision, timeout.Token);
                    break;
                case "transition-complete":
                    Assert.True((await transitions.CompleteAsync(processor, revision.Position, timeout.Token)).IsCompleted);
                    break;
                case "outcome-admission":
                    await admissions.AdmitAsync(processor, revision.Position, proposed, timeout.Token);
                    break;
                case "legacy-outcome":
                    await new SqlServerProductionReferenceTimeOutcomeStore(database.ConnectionString)
                        .PublishAsync(processor, revision.Position, proposed, timeout.Token);
                    break;
                default: throw new InvalidOperationException("Unknown fixture path.");
            }
        }
        var writer = Write();
        Exception? writerFailure = null;
        try
        {
            var wait = await ObserveWait(observer, blockerSpid, writer, timeout.Token);
            Assert.StartsWith("LCK_M_", wait.WaitType, StringComparison.Ordinal);
            Assert.NotEmpty(wait.WaitResource);
            var locks = await ReadLocks(observer, wait.SessionId, blockerSpid, timeout.Token);
            Assert.Contains(locks, row => row.SessionId == wait.SessionId && row.Status == "WAIT");
            if (path is "transition-begin" or "transition-complete" or "outcome-admission")
                Assert.Contains(locks, row => row.SessionId == wait.SessionId && row.Status == "GRANT"
                    && row.ObjectName == "ProductionReferenceTimePublicationTransition");
            output.WriteLine("B1_SOURCE_LOCK_EVIDENCE " + JsonSerializer.Serialize(new
            {
                Path = path, Database = observer.Database, SqlVersion = await SqlVersion(observer),
                BarrierTable = table, Wait = wait, Locks = locks,
                Scope = "source-writer characterization only; no V2 publisher or C1-C8 acceptance",
            }));
        }
        finally
        {
            await transaction.RollbackAsync();
            // Observe writer faults even when evidence assertions fail; no abandoned worker.
            try { await writer.WaitAsync(TimeSpan.FromSeconds(20)); }
            catch (Exception error)
            {
                output.WriteLine("B1_SOURCE_WRITER_ERROR " + error);
                writerFailure = error;
            }
        }
        if (writerFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(writerFailure).Throw();
        var checkpoint = await aggregation.ReadCheckpointAsync(processor, first.StreamId, CancellationToken.None);
        Assert.Equal(next ?? revision, checkpoint);
        if (path is "outcome-admission" or "legacy-outcome" or "transition-complete")
        {
            var outcomes = await new SqlServerProductionReferenceTimeOutcomeStore(database.ConnectionString)
                .ReadAtRevisionAsync(processor, proposed.PublicationRevision, CancellationToken.None);
            Assert.Equal(JsonSerializer.Serialize(proposed), JsonSerializer.Serialize(Assert.Single(outcomes)));
        }
        var transition = await transitions.ReadAsync(processor, revision.Position);
        if (path == "transition-complete") Assert.True(Assert.IsType<ProductionReferenceTimePublicationTransition>(transition).IsCompleted);
        if (path is "transition-begin" or "outcome-admission") Assert.False(Assert.IsType<ProductionReferenceTimePublicationTransition>(transition).IsCompleted);
        output.WriteLine("B1_SOURCE_WRITER_OUTCOME " + JsonSerializer.Serialize(new
        {
            Path = path, Completed = true, AggregationPosition = checkpoint!.Position.Value,
            TransitionCompleted = transition?.IsCompleted, DeadlockRetryCount = 0,
        }));
    }

    private sealed record WaitEvidence(int SessionId, string WaitType, string WaitResource, string SqlText);
    private sealed record LockEvidence(int SessionId, string ResourceType, string Mode, string Status, string Resource, string? ObjectName);

    private static async Task<WaitEvidence> ObserveWait(SqlConnection observer, int blocker, Task writer, CancellationToken token)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(15))
        {
            await using var command = observer.CreateCommand();
            command.CommandText = """
                SELECT r.session_id,r.wait_type,r.wait_resource,t.text
                FROM sys.dm_exec_requests r CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t
                WHERE r.database_id=DB_ID() AND r.blocking_session_id=@Blocker;
                """;
            command.Parameters.Add("@Blocker", SqlDbType.Int).Value = blocker;
            await using (var reader = await command.ExecuteReaderAsync(token))
                if (await reader.ReadAsync(token)) return new(reader.GetInt16(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
            if (writer.IsCompleted) { await writer; throw new InvalidOperationException("Writer completed without reaching its SQL wait barrier."); }
            await Task.Delay(50, token); // Poll observed SQL state; elapsed time alone never satisfies the barrier.
        }
        throw new TimeoutException("No matching blocked writer observed within fixture bound.");
    }

    private static async Task<List<LockEvidence>> ReadLocks(SqlConnection observer, int writer, int blocker, CancellationToken token)
    {
        await using var command = observer.CreateCommand();
        command.CommandText = """
            SELECT l.request_session_id,l.resource_type,l.request_mode,l.request_status,
                   l.resource_description,
                   OBJECT_NAME(CASE WHEN l.resource_type='OBJECT' THEN CONVERT(int,l.resource_associated_entity_id) ELSE p.object_id END)
            FROM sys.dm_tran_locks l
            LEFT JOIN sys.partitions p ON p.hobt_id=l.resource_associated_entity_id AND l.resource_type IN ('KEY','PAGE','RID','HOBT')
            WHERE l.resource_database_id=DB_ID() AND l.request_session_id IN (@Writer,@Blocker)
            ORDER BY l.request_session_id,l.resource_type,l.resource_associated_entity_id,l.request_mode;
            """;
        command.Parameters.Add("@Writer", SqlDbType.Int).Value = writer;
        command.Parameters.Add("@Blocker", SqlDbType.Int).Value = blocker;
        var rows = new List<LockEvidence>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) rows.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? string.Empty : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        return rows;
    }

    private static async Task<string> SqlVersion(SqlConnection observer)
    {
        await using var command = observer.CreateCommand();
        command.CommandText = "SELECT CONVERT(nvarchar(128),SERVERPROPERTY('ProductVersion'));";
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
