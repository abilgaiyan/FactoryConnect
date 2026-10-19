using System.Data;
using System.Diagnostics;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlContainmentArchiveIntegrationTests(SqlContainmentAuthorityDatabaseFixture fixture)
    : IClassFixture<SqlContainmentAuthorityDatabaseFixture>
{
    private sealed record Seed(ReadOnlyMemory<byte>[] Envelopes, MetricAggregationCheckpoint Cut, long Processor);

    [Fact]
    public async Task SharedRootAndNestedWitnessesArchiveExactlyAndReplayWithoutWrites()
    {
        var seed = await SeedAsync();
        await using var connection = fixture.CreateConnection(); await connection.OpenAsync();
        await using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
        {
            var receipts = await SqlContainmentArchiveTransaction.PrepareAsync(connection, transaction, seed.Envelopes, false, default);
            Assert.Equal(2, receipts.Count);
            Assert.Equal((1, 2, 2, 0), await Counts(connection, transaction, seed));
            Assert.Same(connection, transaction.Connection);
            await transaction.CommitAsync();
        }
        await using var replay = connection.BeginTransaction(IsolationLevel.Serializable);
        var before = await Snapshot(connection, replay);
        _ = await SqlContainmentArchiveTransaction.PrepareAsync(connection, replay, seed.Envelopes, true, default,
            (_, _) => throw new InvalidOperationException("Replay must not enter mutation hooks."));
        Assert.Equal(before, await Snapshot(connection, replay));
        await using var query = connection.CreateCommand(); query.Transaction = replay;
        query.CommandText = """
            SELECT r.ConflictWitnessId,x.NodePath,x.WitnessId,r.ResultPayload
            FROM dbo.OperationalMetricPublicationResultArchive r
            JOIN dbo.OperationalMetricResultWitnessReference x ON x.ResultId=r.ResultId
            WHERE r.OperationalMetricProjectionProcessorRowId=@P ORDER BY r.ResultId;
            """;
        query.Parameters.AddWithValue("@P", seed.Processor);
        await using var rows = await query.ExecuteReaderAsync();
        var seen = 0;
        while (await rows.ReadAsync())
        {
            var document = OperationalMetricDependencySnapshotV2Codec.Decode((byte[])rows[3]);
            var path = OperationalMetricWitnessNodePathCodec.Decode((byte[])rows[1]);
            if (document.Dependencies.Count == 0) { Assert.Empty(path); Assert.Equal((byte[])rows[0], (byte[])rows[2]); }
            else { Assert.True(rows.IsDBNull(0)); Assert.Equal(new uint[] { 0 }, path); }
            seen++;
        }
        Assert.Equal(2, seen);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task FailureAtEveryArchiveStageRollsBackTheWholeCallerTransaction(int stage)
    {
        var seed = await SeedAsync();
        await using var connection = fixture.CreateConnection(); await connection.OpenAsync();
        var before = await Snapshot(connection, null);
        await using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => SqlContainmentArchiveTransaction.PrepareAsync(connection, transaction,
                seed.Envelopes, false, default, (current, _) => current == (ContainmentArchiveStage)stage
                    ? Task.FromException(new InvalidOperationException("P4 injected archive failure")) : Task.CompletedTask));
            await transaction.RollbackAsync();
        }
        Assert.Equal(before, await Snapshot(connection, null));
    }

    [Fact]
    public async Task SuccessfulPreparationCanBeRolledBackByCallerWithoutAnyCheckpoint()
    {
        var seed = await SeedAsync();
        await using var connection = fixture.CreateConnection(); await connection.OpenAsync();
        var before = await Snapshot(connection, null);
        await using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
        {
            _ = await SqlContainmentArchiveTransaction.PrepareAsync(connection, transaction, seed.Envelopes, false, default);
            await transaction.RollbackAsync();
        }
        Assert.Equal(before, await Snapshot(connection, null));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PreflightRejectsMalformedOrDuplicateInputsBeforeWrites(bool duplicate)
    {
        var seed = await SeedAsync();
        await using var connection = fixture.CreateConnection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var before = await Snapshot(connection, transaction);
        if (duplicate) await Assert.ThrowsAsync<ArgumentException>(() => SqlContainmentArchiveTransaction.PrepareAsync(connection, transaction,
            [seed.Envelopes[0], seed.Envelopes[0]], false, default));
        else await Assert.ThrowsAsync<InvalidDataException>(() => SqlContainmentArchiveTransaction.PrepareAsync(connection, transaction,
            [seed.Envelopes[0][..^1]], false, default));
        Assert.Equal(before, await Snapshot(connection, transaction));
    }

    [Fact]
    public async Task CanonicalForgeryInALaterResultPreventsEveryArchiveWrite()
    {
        var seed = await SeedAsync();
        await using var connection = fixture.CreateConnection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var before = await Snapshot(connection, transaction);
        await Assert.ThrowsAsync<ContainmentHistoricalIntegrityException>(() => SqlContainmentArchiveTransaction.PrepareAsync(connection,
            transaction, [seed.Envelopes[0], ForgeOee(seed.Envelopes[1])], false, default));
        Assert.Equal(before, await Snapshot(connection, transaction));
    }

    [Fact]
    public async Task ChangedCanonicalContentInAnExistingSlotConflictsWithoutMutation()
    {
        var seed = await SeedAsync(); await Commit(seed);
        await using var connection = fixture.CreateConnection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var before = await Snapshot(connection, transaction);
        await Assert.ThrowsAsync<ContainmentPublicationConflictException>(() => SqlContainmentArchiveTransaction.PrepareAsync(connection,
            transaction, [seed.Envelopes[0], ForgeOee(seed.Envelopes[1])], false, default));
        Assert.Equal(before, await Snapshot(connection, transaction));
    }

    [Theory]
    [InlineData("reference")] [InlineData("witness")] [InlineData("result-index")]
    public async Task ReplayRejectsCorruptionAndNeverRepairsArchiveContent(string corruption)
    {
        var seed = await SeedAsync(); await Commit(seed);
        await using var connection = fixture.CreateConnection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        await using (var mutate = connection.CreateCommand())
        {
            mutate.Transaction = transaction;
            mutate.Parameters.AddWithValue("@P", seed.Processor);
            mutate.CommandText = corruption switch
            {
                "reference" => "DISABLE TRIGGER dbo.TR_OMRWR_Immutable ON dbo.OperationalMetricResultWitnessReference; DELETE x FROM dbo.OperationalMetricResultWitnessReference x JOIN dbo.OperationalMetricPublicationResultArchive r ON r.ResultId=x.ResultId WHERE r.OperationalMetricProjectionProcessorRowId=@P;",
                "witness" => "DISABLE TRIGGER dbo.TR_OMCWA_Immutable ON dbo.OperationalMetricConflictWitnessArchive; UPDATE w SET WitnessPayload=0x01 FROM dbo.OperationalMetricConflictWitnessArchive w JOIN dbo.OperationalMetricProjectionProcessor p ON p.MetricAggregationProcessorRowId=w.MetricAggregationProcessorRowId WHERE p.OperationalMetricProjectionProcessorRowId=@P;",
                _ => "DISABLE TRIGGER dbo.TR_OMPRA_Immutable ON dbo.OperationalMetricPublicationResultArchive; UPDATE dbo.OperationalMetricPublicationResultArchive SET EvaluationKeyBinary=0x01 WHERE OperationalMetricProjectionProcessorRowId=@P;"
            };
            await mutate.ExecuteNonQueryAsync();
        }
        var corrupt = await Snapshot(connection, transaction);
        await Assert.ThrowsAsync<ContainmentHistoricalIntegrityException>(() => SqlContainmentArchiveTransaction.PrepareAsync(connection,
            transaction, seed.Envelopes, true, default));
        Assert.Equal(corrupt, await Snapshot(connection, transaction));
        await transaction.RollbackAsync(); // Restore both immutable triggers and original rows.
    }

    [Fact]
    public async Task FormatOneRegistrationCannotReceiveContainmentArchives()
    {
        var seed = await SeedAsync(format: 1);
        await using var connection = fixture.CreateConnection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var before = await Snapshot(connection, transaction);
        await Assert.ThrowsAsync<ContainmentHistoricalIntegrityException>(() => SqlContainmentArchiveTransaction.PrepareAsync(connection,
            transaction, seed.Envelopes, false, default));
        Assert.Equal(before, await Snapshot(connection, transaction));
    }

    [Fact]
    public async Task CallerMustSupplySerializableAndReplayCannotCreateMissingResults()
    {
        var seed = await SeedAsync();
        await using var connection = fixture.CreateConnection(); await connection.OpenAsync();
        await using (var unstable = connection.BeginTransaction(IsolationLevel.ReadCommitted))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => SqlContainmentArchiveTransaction.PrepareAsync(connection,
                unstable, seed.Envelopes, false, default));
            await unstable.RollbackAsync();
        }
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        await Assert.ThrowsAsync<ContainmentHistoricalIntegrityException>(() => SqlContainmentArchiveTransaction.PrepareAsync(connection,
            transaction, seed.Envelopes, true, default));
        Assert.Equal((0, 0, 0, 0), await Counts(connection, transaction, seed));
    }

    [Fact]
    public async Task CancellationAfterArchiveDmlLeavesRollbackWithTheCaller()
    {
        var seed = await SeedAsync();
        await using var connection = fixture.CreateConnection(); await connection.OpenAsync();
        var before = await Snapshot(connection, null);
        using var cancellation = new CancellationTokenSource();
        await using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SqlContainmentArchiveTransaction.PrepareAsync(connection,
                transaction, seed.Envelopes, false, cancellation.Token, (stage, _) =>
                { if (stage == ContainmentArchiveStage.WitnessesInserted) cancellation.Cancel(); return Task.CompletedTask; }));
            await transaction.RollbackAsync();
        }
        Assert.Equal(before, await Snapshot(connection, null));
    }

    [Fact]
    public async Task ConcurrentIdenticalArchivePreparationsSerializeAndReuseOneImmutableSet()
    {
        var seed = await SeedAsync();
        await using var first = fixture.CreateConnection(); await first.OpenAsync();
        await using var transaction = first.BeginTransaction(IsolationLevel.Serializable);
        _ = await SqlContainmentArchiveTransaction.PrepareAsync(first, transaction, seed.Envelopes, false, default);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var second = Task.Run(async () =>
        {
            await using var connection = fixture.CreateConnection(); await connection.OpenAsync(cancellation.Token);
            await using var next = connection.BeginTransaction(IsolationLevel.Serializable);
            _ = await SqlContainmentArchiveTransaction.PrepareAsync(connection, next, seed.Envelopes, true, cancellation.Token);
            await next.CommitAsync(cancellation.Token);
        }, cancellation.Token);
        try
        {
            await using var session = first.CreateCommand(); session.Transaction = transaction; session.CommandText = "SELECT @@SPID;";
            var spid = Convert.ToInt32(await session.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
            await using var monitor = fixture.CreateConnection(); await monitor.OpenAsync();
            await using var waiting = monitor.CreateCommand();
            waiting.CommandText = "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id=@Reader AND database_id=DB_ID();";
            waiting.Parameters.AddWithValue("@Reader", spid);
            var timer = Stopwatch.StartNew(); var observed = false;
            while (timer.Elapsed < TimeSpan.FromSeconds(10))
            {
                if ((int)(await waiting.ExecuteScalarAsync())! > 0) { observed = true; break; }
                if (second.IsCompleted) await second;
                await Task.Delay(25);
            }
            Assert.True(observed, "Expected the second archive writer to wait on the first caller transaction.");
        }
        finally { await transaction.CommitAsync(); await second.WaitAsync(TimeSpan.FromSeconds(15)); }
        await using var check = fixture.CreateConnection(); await check.OpenAsync();
        Assert.Equal((1, 2, 2, 0), await Counts(check, null, seed));
    }

    private async Task<Seed> SeedAsync(short format = 2)
    {
        var (envelopes, cut) = await new SqlContainmentHistoricalResultIntegrationTests(fixture).CreateArchiveTestEvidenceAsync();
        var processor = OperationalMetricDependencySnapshotV2Codec.Decode(envelopes[0].Span).ProcessorId;
        await using var connection = fixture.CreateConnection(); await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT dbo.OperationalMetricProjectionProcessor
              (ProcessorKeyBinary,ProcessorKey,MetricAggregationProcessorRowId,MetricInputStreamRowId,PublicationFormatVersion,EvaluationPolicyId)
            OUTPUT INSERTED.OperationalMetricProjectionProcessorRowId
            SELECT @Key,@Processor,MetricAggregationProcessorRowId,MetricInputStreamRowId,@Format,@Policy
            FROM dbo.MetricAggregationProcessor WHERE ProcessorKeyBinary=@AP;
            """;
        command.Parameters.Add("@Key", SqlDbType.VarBinary, StringOrderKeyV2Codec.MaximumEncodedLength).Value = StringOrderKeyV2Codec.Encode(processor.Value);
        command.Parameters.AddWithValue("@Processor", processor.Value);
        command.Parameters.Add("@AP", SqlDbType.VarBinary, 512).Value = OrdinalStringKeyCodec.Encode(cut.ProcessorId.Value);
        command.Parameters.AddWithValue("@Format", format);
        command.Parameters.Add("@Policy", SqlDbType.NVarChar, 64).Value = format == 2 ? AvailabilityContainmentV1.PolicyId : DBNull.Value;
        return new(envelopes, cut, (long)(await command.ExecuteScalarAsync())!);
    }
    private async Task Commit(Seed seed)
    {
        await using var connection = fixture.CreateConnection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        _ = await SqlContainmentArchiveTransaction.PrepareAsync(connection, transaction, seed.Envelopes, false, default);
        await transaction.CommitAsync();
    }
    private static ReadOnlyMemory<byte> ForgeOee(ReadOnlyMemory<byte> payload)
    {
        var document = OperationalMetricDependencySnapshotV2Codec.Decode(payload.Span);
        var children = document.Dependencies.ToArray(); var quality = children[2].Result; var components = quality.Components.ToArray(); var first = components[0];
        components[0] = new(first.OperandName,first.SourceIdentity,first.SourceRevision,first.Dimension,1m,first.Unit,first.InputCount,first.FirstInputTimestamp,first.LastInputTimestamp);
        children[2] = children[2] with { Result = new(quality.ProcessorId,quality.Key,quality.Status,0.5m,quality.Unit,quality.ReasonCode,
            quality.ReasonOperandName,quality.SourceRevision,quality.Authority,quality.Witness,components,quality.Dependencies) };
        return OperationalMetricDependencySnapshotV2Codec.Encode(new(document.ProcessorId,document.Key,document.Status,document.Value,document.Unit,
            document.ReasonCode,document.ReasonOperandName,document.SourceRevision,document.Authority,document.Witness,document.Components,children));
    }
    private static async Task<(int,int,int,int)> Counts(SqlConnection connection, SqlTransaction? transaction, Seed seed)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM dbo.OperationalMetricConflictWitnessArchive w JOIN dbo.OperationalMetricProjectionProcessor p ON p.MetricAggregationProcessorRowId=w.MetricAggregationProcessorRowId WHERE p.OperationalMetricProjectionProcessorRowId=@P),
              (SELECT COUNT(*) FROM dbo.OperationalMetricPublicationResultArchive WHERE OperationalMetricProjectionProcessorRowId=@P),
              (SELECT COUNT(*) FROM dbo.OperationalMetricResultWitnessReference x JOIN dbo.OperationalMetricPublicationResultArchive r ON r.ResultId=x.ResultId WHERE r.OperationalMetricProjectionProcessorRowId=@P),
              (SELECT COUNT(*) FROM dbo.OperationalMetricProjectionCheckpoint WHERE OperationalMetricProjectionProcessorRowId=@P)
              +(SELECT COUNT(*) FROM dbo.OperationalMetricProjection WHERE OperationalMetricProjectionProcessorRowId=@P);
            """;
        command.Parameters.AddWithValue("@P", seed.Processor);
        await using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
        return (reader.GetInt32(0),reader.GetInt32(1),reader.GetInt32(2),reader.GetInt32(3));
    }
    private static async Task<string> Snapshot(SqlConnection connection, SqlTransaction? transaction)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            DECLARE @Snapshot nvarchar(max) = (SELECT
              (SELECT * FROM dbo.OperationalMetricConflictWitnessArchive ORDER BY WitnessId FOR JSON PATH,INCLUDE_NULL_VALUES) AS Witnesses,
              (SELECT * FROM dbo.OperationalMetricPublicationResultArchive ORDER BY ResultId FOR JSON PATH,INCLUDE_NULL_VALUES) AS Results,
              (SELECT * FROM dbo.OperationalMetricResultWitnessReference ORDER BY ResultId,NodePath FOR JSON PATH) AS WitnessReferences,
              (SELECT * FROM dbo.OperationalMetricProjectionProcessor ORDER BY OperationalMetricProjectionProcessorRowId FOR JSON PATH,INCLUDE_NULL_VALUES) AS Processors,
              (SELECT * FROM dbo.OperationalMetricProjectionCheckpoint ORDER BY OperationalMetricProjectionProcessorRowId FOR JSON PATH) AS Checkpoints,
              (SELECT * FROM dbo.OperationalMetricProjection ORDER BY OperationalMetricProjectionRowId FOR JSON PATH) AS Projections,
              (SELECT * FROM dbo.OperationalMetricProjectionManifest ORDER BY OperationalMetricProjectionProcessorRowId,OperationalMetricProjectionRowId FOR JSON PATH) AS Manifest,
              (SELECT * FROM dbo.OperationalMetricProjectionEvidence ORDER BY OperationalMetricProjectionEvidenceRowId FOR JSON PATH) AS Evidence
            FOR JSON PATH); SELECT @Snapshot;
            """;
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
