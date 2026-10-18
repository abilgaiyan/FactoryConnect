using System.Data;
using System.Diagnostics;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlContainmentPublicationIntegrationTests(SqlContainmentAuthorityDatabaseFixture fixture)
    : IClassFixture<SqlContainmentAuthorityDatabaseFixture>
{
    private SqlContainmentPublicationTransaction Publisher => new(fixture.ConnectionString);
    private Task<(ReadOnlyMemory<byte>[] Envelopes, MetricAggregationCheckpoint Cut)> Seed() =>
        new SqlContainmentHistoricalResultIntegrationTests(fixture).CreateArchiveTestEvidenceAsync();

    [Fact]
    public async Task PublishesCompleteFormatTwoStateAndExactReplayDoesNoDml()
    {
        var (envelopes, cut) = await Seed();
        await Publisher.PublishAsync(envelopes, null, default);
        var batch = ContainmentArchiveBatch.Preflight(envelopes);
        await using var c = fixture.CreateConnection(); await c.OpenAsync();
        await using var tx = c.BeginTransaction(IsolationLevel.Serializable);
        var before = await Snapshot(c, tx);
        var rows = await Read(c, tx, "SELECT * FROM dbo.OperationalMetricProjection WHERE OperationalMetricProjectionProcessorRowId=(SELECT OperationalMetricProjectionProcessorRowId FROM dbo.OperationalMetricProjectionProcessor WHERE ProcessorKey=@Name) ORDER BY EvaluationKeyHash;", batch.Processor.Value);
        Assert.Equal(2, rows.Count);
        foreach (var row in rows)
        {
            var expected = batch.Results.Single(r => ((byte[])row["ContainmentResultId"]!).AsSpan().SequenceEqual(r.Id));
            ContainmentRetainedState.Projection((long)row["OperationalMetricProjectionProcessorRowId"]!, expected).Match(row);
            var evidence = await Read(c, tx, "SELECT * FROM dbo.OperationalMetricProjectionEvidence WHERE OperationalMetricProjectionRowId=@Id ORDER BY EvidenceKind,EvidenceOrdinal;", id: (long)row["OperationalMetricProjectionRowId"]!);
            var expectedEvidence = ContainmentRetainedState.Evidence((long)row["OperationalMetricProjectionRowId"]!, expected.Document);
            Assert.Equal(expectedEvidence.Length, evidence.Count);
            for (var i = 0; i < evidence.Count; i++) expectedEvidence[i].Match(evidence[i]);
        }
        await tx.CommitAsync();
        await Publisher.PublishAsync(envelopes, null, default, (_, _) => throw new InvalidOperationException("Replay performed DML"));
        Assert.Equal(before, await Snapshot(c, null));
        var checkpoint = new OperationalMetricProjectionCheckpoint(batch.Processor, cut,
            new OperationalMetricProjectionBatchManifest(batch.Results.Select(r => r.Document.Key)));
        await Publisher.PublishAsync(envelopes, checkpoint, default);
        Assert.Equal(before, await Snapshot(c, null));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)]
    public async Task FailureAtEveryPublicationStageRollsBackRegistrationArchivesRetainedAndCheckpoint(int stage)
    {
        var (envelopes, _) = await Seed();
        await using var c = fixture.CreateConnection(); await c.OpenAsync(); var before = await Snapshot(c, null);
        var reached = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Publisher.PublishAsync(envelopes, null, default,
            (current, _) => { if ((int)current == stage) { reached = true; throw new InvalidOperationException("Injected publication failure"); } return Task.CompletedTask; }));
        Assert.True(reached); Assert.Equal(before, await Snapshot(c, null));
        await Publisher.PublishAsync(envelopes, null, default); // Retry must succeed from completely rolled-back state.
    }

    [Theory]
    [InlineData("projection")] [InlineData("manifest")] [InlineData("component")]
    [InlineData("dependency")] [InlineData("evidence-missing")]
    public async Task ReplayRejectsDamagedRetainedStateWithoutRepair(string corruption)
    {
        var (envelopes, _) = await Seed(); await Publisher.PublishAsync(envelopes, null, default);
        var name = ContainmentArchiveBatch.Preflight(envelopes).Processor.Value;
        await using var c = fixture.CreateConnection(); await c.OpenAsync();
        await using var command = c.CreateCommand(); command.Parameters.AddWithValue("@Name", name);
        var filter = " IN (SELECT OperationalMetricProjectionRowId FROM dbo.OperationalMetricProjection WHERE OperationalMetricProjectionProcessorRowId=(SELECT OperationalMetricProjectionProcessorRowId FROM dbo.OperationalMetricProjectionProcessor WHERE ProcessorKey=@Name))";
        command.CommandText = corruption switch
        {
            "projection" => "UPDATE dbo.OperationalMetricProjection SET Unit=N'forged' WHERE OperationalMetricProjectionRowId" + filter,
            "manifest" => "DELETE dbo.OperationalMetricProjectionManifest WHERE OperationalMetricProjectionRowId" + filter,
            "component" => "UPDATE dbo.OperationalMetricProjectionEvidence SET ComponentValue=N'999' WHERE EvidenceKind=1 AND OperationalMetricProjectionRowId" + filter,
            "dependency" => "UPDATE dbo.OperationalMetricProjectionEvidence SET DependencySnapshotHash=0x0000000000000000000000000000000000000000000000000000000000000000 WHERE EvidenceKind=2 AND OperationalMetricProjectionRowId" + filter,
            _ => "DELETE dbo.OperationalMetricProjectionEvidence WHERE OperationalMetricProjectionRowId" + filter
        };
        Assert.True(await command.ExecuteNonQueryAsync() > 0);
        var damaged = await Snapshot(c, null);
        await Assert.ThrowsAsync<ContainmentHistoricalIntegrityException>(() => Publisher.PublishAsync(envelopes, null, default));
        Assert.Equal(damaged, await Snapshot(c, null));
    }

    [Fact]
    public async Task CancellationAfterCheckpointWriteRollsBackEverything()
    {
        var (envelopes, _) = await Seed();
        await using var c = fixture.CreateConnection(); await c.OpenAsync(); var before = await Snapshot(c, null);
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Publisher.PublishAsync(envelopes, null, cancel.Token,
            (stage, _) => { if (stage == ContainmentPublicationStage.CheckpointWritten) cancel.Cancel(); return Task.CompletedTask; }));
        Assert.Equal(before, await Snapshot(c, null));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ConcurrentWritersAndStableReaderObserveOnlyCompletePublication(bool conflict)
    {
        var (envelopes, _) = await Seed(); var batch = ContainmentArchiveBatch.Preflight(envelopes);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Publisher.PublishAsync(envelopes, null, timeout.Token, async (stage, ct) =>
        { if (stage == ContainmentPublicationStage.ReferencesInserted) { reached.SetResult(); await release.Task.WaitAsync(ct); } });
        await reached.Task.WaitAsync(timeout.Token);
        var proposed = conflict ? new[] { envelopes[0] } : envelopes;
        var second = Publisher.PublishAsync(proposed, null, timeout.Token);
        var reader = Task.Run(async () =>
        {
            await using var c = fixture.CreateConnection(); await c.OpenAsync(timeout.Token);
            await using var tx = c.BeginTransaction(IsolationLevel.Serializable);
            await SqlServerOperationalMetricProjectionProcessorGate.AcquireAsync(c, tx, batch.Processor,
                OperationalMetricProjectionProcessorGateMode.Shared, timeout.Token);
            var rows = await Read(c, tx, "SELECT * FROM dbo.OperationalMetricProjection WHERE OperationalMetricProjectionProcessorRowId=(SELECT OperationalMetricProjectionProcessorRowId FROM dbo.OperationalMetricProjectionProcessor WHERE ProcessorKey=@Name);", batch.Processor.Value);
            Assert.Equal(2, rows.Count); await tx.CommitAsync(timeout.Token);
        }, timeout.Token);
        try
        {
            await using var monitor = fixture.CreateConnection(); await monitor.OpenAsync(timeout.Token);
            await using var query = monitor.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE database_id=DB_ID() AND wait_type LIKE 'LCK_M_%' AND blocking_session_id<>0;";
            var timer = Stopwatch.StartNew(); var observed = false;
            while (timer.Elapsed < TimeSpan.FromSeconds(10))
            {
                if ((int)(await query.ExecuteScalarAsync(timeout.Token))! >= 2) { observed = true; break; }
                await Task.Delay(25, timeout.Token);
            }
            Assert.True(observed, "Both the second publisher and stable reader must wait behind the unpublished transaction.");
            Assert.False(second.IsCompleted); Assert.False(reader.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await first; await reader;
        if (conflict) await Assert.ThrowsAsync<ContainmentHistoricalIntegrityException>(() => second);
        else await second;
        await Publisher.PublishAsync(envelopes, null, default);
    }

    [Theory]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)]
    public async Task AdvancementPreservesUnchangedRowsAndArchivesAndRejectsBackwardReplay(int failStage)
    {
        var (first, next) = await new SqlContainmentHistoricalResultIntegrationTests(fixture).CreatePublicationAdvanceEvidenceAsync();
        var prior = ContainmentArchiveBatch.Preflight(first); var proposed = ContainmentArchiveBatch.Preflight(next);
        await Publisher.PublishAsync(first, null, default);
        var expected = new OperationalMetricProjectionCheckpoint(prior.Processor, prior.Cut,
            new OperationalMetricProjectionBatchManifest(prior.Results.Select(r => r.Document.Key)));
        await using var c = fixture.CreateConnection(); await c.OpenAsync();
        var before = await Snapshot(c, null);
        await Assert.ThrowsAsync<ContainmentPublicationConflictException>(() => Publisher.PublishAsync(next, null, default));
        Assert.Equal(before, await Snapshot(c, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Publisher.PublishAsync(next, expected, default,
            (stage, _) => (int)stage == failStage ? Task.FromException(new InvalidOperationException("Rollback updated row")) : Task.CompletedTask));
        Assert.Equal(before, await Snapshot(c, null));
        await Publisher.PublishAsync(next, expected, default);
        var rows = await Read(c, null, "SELECT * FROM dbo.OperationalMetricProjection WHERE OperationalMetricProjectionProcessorRowId=(SELECT OperationalMetricProjectionProcessorRowId FROM dbo.OperationalMetricProjectionProcessor WHERE ProcessorKey=@Name);", prior.Processor.Value);
        Assert.Equal(2, rows.Count);
        var changed = rows.Single(r => ((byte[])r["EvaluationKeyHash"]!).AsSpan().SequenceEqual(proposed.Results[0].KeyHash));
        ContainmentRetainedState.Projection((long)changed["OperationalMetricProjectionProcessorRowId"]!, proposed.Results[0]).Match(changed);
        var unchanged = rows.Single(r => !ReferenceEquals(r, changed));
        var old = prior.Results.Single(r => r.Document.Key != proposed.Results[0].Document.Key);
        ContainmentRetainedState.Projection((long)unchanged["OperationalMetricProjectionProcessorRowId"]!, old).Match(unchanged);
        var committed = await Snapshot(c, null);
        await Publisher.PublishAsync(next, expected, default, (_, _) => throw new InvalidOperationException("Replay mutated"));
        await Assert.ThrowsAsync<ContainmentPublicationConflictException>(() => Publisher.PublishAsync(first, null, default));
        Assert.Equal(committed, await Snapshot(c, null));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ExistingArchiveOnlyOrLegacyRegistrationCannotBeRepairedOrRebound(bool legacy)
    {
        var (envelopes, _) = await Seed(); var batch = ContainmentArchiveBatch.Preflight(envelopes);
        await using var c = fixture.CreateConnection(); await c.OpenAsync();
        await using (var command = c.CreateCommand())
        {
            command.CommandText = """
                INSERT dbo.OperationalMetricProjectionProcessor
                  (ProcessorKeyBinary,ProcessorKey,MetricAggregationProcessorRowId,MetricInputStreamRowId,PublicationFormatVersion,EvaluationPolicyId)
                SELECT @Key,@Name,MetricAggregationProcessorRowId,MetricInputStreamRowId,@Format,@Policy
                FROM dbo.MetricAggregationProcessor WHERE ProcessorKeyBinary=@AP;
                """;
            command.Parameters.Add("@Key", SqlDbType.VarBinary, StringOrderKeyV2Codec.MaximumEncodedLength).Value = StringOrderKeyV2Codec.Encode(batch.Processor.Value);
            command.Parameters.AddWithValue("@Name", batch.Processor.Value);
            command.Parameters.Add("@AP", SqlDbType.VarBinary, 512).Value = OrdinalStringKeyCodec.Encode(batch.Cut.ProcessorId.Value);
            command.Parameters.AddWithValue("@Format", legacy ? (short)1 : (short)2);
            command.Parameters.Add("@Policy", SqlDbType.NVarChar, 64).Value = legacy ? DBNull.Value : AvailabilityContainmentV1.PolicyId;
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        if (!legacy)
        {
            await using var tx = c.BeginTransaction(IsolationLevel.Serializable);
            _ = await SqlContainmentArchiveTransaction.PrepareAsync(c, tx, envelopes, false, default);
            await tx.CommitAsync();
        }
        var before = await Snapshot(c, null);
        await Assert.ThrowsAsync<ContainmentHistoricalIntegrityException>(() => Publisher.PublishAsync(envelopes, null, default));
        Assert.Equal(before, await Snapshot(c, null));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CompletedPublisherHoldsHistoricalSourceWritersThroughCommit(bool standard)
    {
        var (envelopes, _) = await Seed(); var batch = ContainmentArchiveBatch.Preflight(envelopes);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publish = Publisher.PublishAsync(envelopes, null, timeout.Token, async (stage, ct) =>
        { if (stage == ContainmentPublicationStage.CheckpointWritten) { reached.SetResult(); await release.Task.WaitAsync(ct); } });
        await reached.Task.WaitAsync(timeout.Token);
        try
        {
            await using var writer = fixture.CreateConnection(); await writer.OpenAsync(timeout.Token);
            await using var command = writer.CreateCommand();
            command.Parameters.AddWithValue("@AP", batch.Cut.ProcessorId.Value);
            command.CommandText = "SET LOCK_TIMEOUT 500; " + (standard ? """
                UPDATE dbo.ProductionStandardVersion SET SourceReference=SourceReference
                WHERE VersionId IN (SELECT SelectedStandardVersionId FROM dbo.ProductionReferenceTimeOutcome
                WHERE MetricAggregationProcessorRowId=(SELECT MetricAggregationProcessorRowId FROM dbo.MetricAggregationProcessor WHERE ProcessorKey=@AP));
                """ : """
                UPDATE dbo.MetricInputFact SET MetricValue=MetricValue
                WHERE MetricInputFactRowId IN (SELECT MetricInputFactRowId FROM dbo.MetricAggregationContribution
                WHERE MetricAggregationProcessorRowId=(SELECT MetricAggregationProcessorRowId FROM dbo.MetricAggregationProcessor WHERE ProcessorKey=@AP));
                """);
            var error = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync(timeout.Token));
            Assert.Equal(1222, error.Number);
            release.TrySetResult(); await publish;
            Assert.True(await command.ExecuteNonQueryAsync(timeout.Token) > 0);
        }
        finally { release.TrySetResult(); await publish; }
    }

    private static async Task<List<Dictionary<string, object?>>> Read(SqlConnection c, SqlTransaction? tx, string sql, string? name = null, long? id = null)
    {
        await using var command = c.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        if (name is not null) command.Parameters.AddWithValue("@Name", name);
        if (id is not null) command.Parameters.AddWithValue("@Id", id.Value);
        return await ContainmentRetainedState.Read(command, default);
    }
    internal static async Task<string> Snapshot(SqlConnection c, SqlTransaction? tx)
    {
        await using var command = c.CreateCommand(); command.Transaction = tx;
        command.CommandText = """
            DECLARE @Snapshot nvarchar(max) = (SELECT
            (SELECT * FROM dbo.OperationalMetricProjectionProcessor ORDER BY OperationalMetricProjectionProcessorRowId FOR JSON PATH,INCLUDE_NULL_VALUES) AS P,
            (SELECT * FROM dbo.OperationalMetricProjectionCheckpoint ORDER BY OperationalMetricProjectionProcessorRowId FOR JSON PATH) AS C,
            (SELECT * FROM dbo.OperationalMetricProjection ORDER BY OperationalMetricProjectionRowId FOR JSON PATH,INCLUDE_NULL_VALUES) AS R,
            (SELECT * FROM dbo.OperationalMetricProjectionEvidence ORDER BY OperationalMetricProjectionEvidenceRowId FOR JSON PATH,INCLUDE_NULL_VALUES) AS E,
            (SELECT * FROM dbo.OperationalMetricProjectionManifest ORDER BY OperationalMetricProjectionProcessorRowId,OperationalMetricProjectionRowId FOR JSON PATH) AS M,
            (SELECT * FROM dbo.OperationalMetricConflictWitnessArchive ORDER BY WitnessId FOR JSON PATH,INCLUDE_NULL_VALUES) AS W,
            (SELECT * FROM dbo.OperationalMetricPublicationResultArchive ORDER BY ResultId FOR JSON PATH,INCLUDE_NULL_VALUES) AS A,
            (SELECT * FROM dbo.OperationalMetricResultWitnessReference ORDER BY ResultId,NodePath FOR JSON PATH) AS X
            FOR JSON PATH); SELECT @Snapshot;
            """;
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
