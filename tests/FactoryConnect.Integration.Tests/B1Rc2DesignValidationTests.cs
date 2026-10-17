using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

// Isolated design-validation probes only. No production migration/codec/publication
// feature is registered. SQL sentinel payloads are deliberately not canonical evidence.
[Trait("Category", "SqlServerIntegration")]
public sealed class B1Rc2DesignValidationTests
{
    [Fact]
    public async Task ExactProposedDdlRollsBackAndThenAppliesWithoutChanging020History()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await SqlServerMigrationEngine.CreateDefault().ApplyAsync(connection, TimeSpan.FromMinutes(2), CancellationToken.None);
        var before = await Scalar(connection, "SELECT STRING_AGG(CONCAT(MigrationId, ':', CanonicalChecksum), ',') WITHIN GROUP (ORDER BY MigrationId) FROM dbo.FactoryConnectMigrationHistory;");
        Assert.Equal(20, SqlMigrationCatalog.Load().Migrations[^1].MigrationId);
        await using (var transaction = connection.BeginTransaction())
        {
            await ApplyProposal(connection, transaction);
            await transaction.RollbackAsync();
        }
        Assert.Equal(0, (int)(await Scalar(connection, "SELECT COUNT(*) FROM sys.tables WHERE name IN ('OperationalMetricConflictWitnessArchive','OperationalMetricPublicationResultArchive','OperationalMetricResultWitnessReference');"))!);
        Assert.Equal(0, (int)(await Scalar(connection, "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID('dbo.OperationalMetricProjection') AND name='PublicationFormatVersion';"))!);
        await using (var transaction = connection.BeginTransaction())
        {
            await ApplyProposal(connection, transaction);
            await transaction.CommitAsync();
        }
        Assert.Equal(before, await Scalar(connection, "SELECT STRING_AGG(CONCAT(MigrationId, ':', CanonicalChecksum), ',') WITHIN GROUP (ORDER BY MigrationId) FROM dbo.FactoryConnectMigrationHistory;"));
        Assert.Equal(3, (int)(await Scalar(connection, "SELECT COUNT(*) FROM sys.tables WHERE name IN ('OperationalMetricConflictWitnessArchive','OperationalMetricPublicationResultArchive','OperationalMetricResultWitnessReference');"))!);
        Assert.Equal(3, (int)(await Scalar(connection, "SELECT COUNT(*) FROM sys.triggers WHERE name IN ('TR_OMCWA_Immutable','TR_OMPRA_Immutable','TR_OMRWR_Immutable') AND is_disabled=0;"))!);
        Assert.Equal(0, (int)(await Scalar(connection, "SELECT COUNT(*) FROM sys.foreign_keys WHERE name IN ('FK_OMCWA_Source','FK_OMCWA_Revision','FK_OMCWA_Contribution','FK_OMCWA_FactPosition','FK_OMPRA_ProcessorFormat','FK_OMPRA_ProcessorSource','FK_OMPRA_Revision','FK_OMPRA_RootWitness','FK_OMRWR_Result','FK_OMRWR_Witness','FK_OMP_ProcessorFormat','FK_OMP_ContainmentResult','FK_OMP_ConflictWitness') AND (is_disabled=1 OR is_not_trusted=1 OR delete_referential_action<>0);"))!);
    }

    [Fact]
    public async Task ActualCheckDefinitionsRejectNullableAndVersionShapeSubstitutions()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await Prepare(connection);
        await Check(connection, "CK_OMPP_FormatPolicy", "PublicationFormatVersion smallint, EvaluationPolicyId nvarchar(64)",
            ["1,NULL", "2,N'availability-scope-containment/1'"], ["2,NULL", "1,N'availability-scope-containment/1'", "3,NULL"]);
        await Check(connection, "CK_OMPRA_WitnessShape", "ReasonCode tinyint, ConflictWitnessId binary(32)",
            ["NULL,NULL", "6,0x01", "2,NULL"], ["NULL,0x01", "6,NULL", "2,0x01"]);
        await Check(connection, "CK_OMP_WitnessShape", "ReasonCode tinyint, ConflictWitnessId binary(32)",
            ["NULL,NULL", "6,0x01", "2,NULL"], ["NULL,0x01", "6,NULL", "2,0x01"]);
        await Check(connection, "CK_OMPRA_StatusReason", "Status tinyint, ReasonCode tinyint",
            ["0,NULL", "1,6", "2,0"], ["1,NULL", "2,NULL", "2,6", "0,0"]);
        await Check(connection, "CK_OMP_FormatPolicy", "PublicationFormatVersion smallint, EvaluationPolicyId nvarchar(64), ContainmentResultId binary(32), ConflictWitnessId binary(32), ReasonCode tinyint",
            ["1,NULL,NULL,NULL,NULL", "1,NULL,NULL,NULL,2", "2,N'availability-scope-containment/1',0x01,NULL,NULL"],
            ["1,NULL,NULL,NULL,6", "2,NULL,0x01,NULL,NULL", "2,N'availability-scope-containment/1',NULL,NULL,NULL", "1,N'availability-scope-containment/1',NULL,NULL,NULL"]);
        const string component = "1,N'duration.running',0,N'1',N's',1,'2026-10-16T00:00:00+00:00','2026-10-16T00:00:01+00:00',NULL,NULL,NULL,NULL,NULL";
        const string dependency = "2,NULL,NULL,NULL,NULL,NULL,NULL,NULL,N'availability',N'1.0',1,0x01,0x01";
        await Check(connection, "CK_OperationalMetricProjectionEvidence_SubtypeShape",
            "EvidenceKind tinyint, ComponentKey nvarchar(256), MetricDimension tinyint, ComponentValue nvarchar(64), ComponentUnit nvarchar(128), InputCount decimal(20,0), FirstInputTimestamp datetimeoffset(7), LastInputTimestamp datetimeoffset(7), DependencyMetricKey nvarchar(256), DependencyDefinitionVersion nvarchar(256), DependencySnapshotCodecVersion smallint, DependencySnapshotHash binary(32), DependencySnapshotBinary varbinary(max)",
            [component, dependency, dependency.Replace(",1,0x01,0x01", ",2,0x01,0x01", StringComparison.Ordinal)],
            [component.Replace("N'duration.running'", "NULL", StringComparison.Ordinal),
             component.Replace("N'1'", "N''", StringComparison.Ordinal),
             component.Replace("N's',1,", "N's',0,", StringComparison.Ordinal),
             component.Replace("00:00:01+00:00", "00:00:01+01:00", StringComparison.Ordinal),
             component.Replace("00:00:01+00:00", "00:00:00+00:01", StringComparison.Ordinal),
             dependency.Replace(",1,0x01,0x01", ",3,0x01,0x01", StringComparison.Ordinal),
             dependency.Replace(",1,0x01,0x01", ",NULL,0x01,0x01", StringComparison.Ordinal),
             dependency.Replace(",1,0x01,0x01", ",1,NULL,0x01", StringComparison.Ordinal),
             dependency.Replace(",1,0x01,0x01", ",1,0x01,0x", StringComparison.Ordinal),
             dependency.Replace("N'availability'", "NULL", StringComparison.Ordinal),
             dependency.Replace("2,NULL,NULL,", "2,N'foreign',NULL,", StringComparison.Ordinal)]);
        var definition = (string)(await Scalar(connection, "SELECT definition FROM sys.check_constraints WHERE name='CK_OperationalMetricProjectionEvidence_SubtypeShape';"))!;
        Assert.Contains("(2)", definition);
        Assert.Contains("DependencySnapshotHash", definition);
        Assert.Contains("LastInputTimestamp", definition);
    }

    [Fact]
    public async Task ArchiveForeignKeysAndActualImmutableTriggersRejectMutations()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await Prepare(connection);
        await Execute(connection, Seed);
        await Reject(connection, "UPDATE dbo.OperationalMetricConflictWitnessArchive SET WitnessCodecVersion=1;", 51021);
        await Reject(connection, "DELETE FROM dbo.OperationalMetricConflictWitnessArchive;", 51021);
        await Reject(connection, Prefix + WitnessInsert.Replace("0x01,1,0x01", "0x02,1,0x01", StringComparison.Ordinal).Replace("@A,@S,2,@F,1", "@A,@S,999,@F,1", StringComparison.Ordinal), 547);
        await Reject(connection, Prefix + WitnessInsert.Replace("0x01,1,0x01", "0x03,1,0x01", StringComparison.Ordinal).Replace("@A,@S,2,@F,1", "@A,@S,2,999999,1", StringComparison.Ordinal), 547);
        await Execute(connection, Prefix + ResultInsert);
        await Reject(connection, "UPDATE dbo.OperationalMetricPublicationResultArchive SET ResultCodecVersion=2;", 51021);
        await Reject(connection, "DELETE FROM dbo.OperationalMetricPublicationResultArchive;", 51021);
        await Reject(connection, Prefix + ResultInsert.Replace("0x01,@P,2", "0x02,@L,2", StringComparison.Ordinal), 547);
        await Reject(connection, Prefix + ResultInsert.Replace("VALUES (0x01,@P,2", "VALUES (0x02,@P,2", StringComparison.Ordinal), 2627);
        await Execute(connection, "INSERT dbo.OperationalMetricResultWitnessReference (ResultId,NodePath,WitnessId) VALUES (0x01,0x00000000,0x01);");
        await Reject(connection, "UPDATE dbo.OperationalMetricResultWitnessReference SET NodePath=0x00000000;", 51021);
        await Reject(connection, "DELETE FROM dbo.OperationalMetricResultWitnessReference;", 51021);
        await Reject(connection, "INSERT dbo.OperationalMetricResultWitnessReference VALUES (0x01,0x00000001,0x99);", 547);
        Assert.Equal(1, (int)(await Scalar(connection, "SELECT COUNT(*) FROM dbo.OperationalMetricConflictWitnessArchive;"))!);
        Assert.Equal(1, (int)(await Scalar(connection, "SELECT COUNT(*) FROM dbo.OperationalMetricPublicationResultArchive;"))!);
        Assert.Equal(1, (int)(await Scalar(connection, "SELECT COUNT(*) FROM dbo.OperationalMetricResultWitnessReference;"))!);
    }

    [Fact]
    public async Task PopulatedLegacyV1PublicationSurvivesSchemaUpgradeAndReplays()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await SqlServerMigrationEngine.CreateDefault().ApplyAsync(connection, TimeSpan.FromMinutes(2), CancellationToken.None);
        var machine = new MachineId(Guid.NewGuid());
        var site = new SiteId("B1-SITE");
        var shift = new ShiftId("B1-SHIFT");
        var schedule = new ShiftScheduleAssignmentId("B1-SCHEDULE");
        var start = new DateTimeOffset(2026, 10, 16, 6, 0, 0, TimeSpan.Zero);
        var occurrence = new ShiftOccurrenceId(site, schedule, shift, start, start.AddHours(8));
        var day = new ProductionDayId(site, new DateOnly(2026, 10, 16));
        var fact = await new SqlServerMetricInputStore(database.ConnectionString).AppendAsync(
            new DurableMetricInputAppend(MetricInputStreamId.ForMachine(machine), new DurableMetricInputFact
            {
                Id = new MetricInputFactId("b1-legacy-fact"), Key = "duration.running", Value = 1m, Unit = "seconds",
                StartsAtUtc = start, EndsAtUtc = start.AddSeconds(1), CompanyId = new CompanyId("B1-COMPANY"),
                SiteId = site, MachineId = machine, ShiftId = shift, ShiftScheduleAssignmentId = schedule,
            }, occurrence, day), CancellationToken.None);
        var aggregation = new MetricAggregationProcessorId("b1-legacy-aggregation");
        var revision = new MetricAggregationCheckpoint(aggregation, fact.StreamId, fact.Position);
        await new SqlServerMetricAggregationStore(database.ConnectionString).CommitAsync(
            new MetricAggregationCommit(aggregation, null, revision, []), CancellationToken.None);
        var processor = new OperationalMetricProjectionProcessorId("b1-legacy-projection");
        var period = new OperationalMetricPeriodId.Shift(occurrence);
        var definition = new OperationalMetricDefinitionId("availability", "1");
        var key = new OperationalMetricEvaluationKey(machine, period, definition, OperationalMetricEvaluationContextKey.Unpartitioned);
        var component = new OperationalMetricComponentProjectionEvidence("runtime",
            new OperationalMetricAggregateSourceIdentity(aggregation, machine, period, "duration.running"),
            revision, MetricDimension.Duration, 1m, "seconds", 1, start, start.AddSeconds(1));
        var availability = new OperationalMetricProjection(processor, key, OperationalMetricEvaluationStatus.Calculated,
            0.75m, "ratio", null, null, revision, [component]);
        var parentKey = new OperationalMetricEvaluationKey(machine, period,
            new OperationalMetricDefinitionId("oee", "1"), OperationalMetricEvaluationContextKey.Unpartitioned);
        var parent = new OperationalMetricProjection(processor, parentKey, OperationalMetricEvaluationStatus.Calculated,
            0.75m, "ratio", null, null, revision,
            dependencyEvidence: [new OperationalMetricDependencyProjectionEvidence("Availability", definition, availability)]);
        var checkpoint = new OperationalMetricProjectionCheckpoint(processor, revision,
            new OperationalMetricProjectionBatchManifest([key, parentKey]));
        var commit = new OperationalMetricProjectionCommit(processor, null, checkpoint, [availability, parent]);
        async Task Publish()
        {
            var transaction = new SqlServerOperationalMetricProjectionCommitTransaction(database.ConnectionString);
            await transaction.ExecuteAsync(commit,
                async (context, token) => await SqlServerOperationalMetricProjectionPublication.ExecuteAsync(context, commit, token),
                CancellationToken.None);
        }
        await Publish();
        var before = OperationalMetricDependencySnapshotV1Codec.Encode(parent);
        await using (var transaction = connection.BeginTransaction())
        {
            await ApplyProposal(connection, transaction);
            await transaction.CommitAsync();
        }
        var actual = await new SqlServerOperationalMetricProjectionQueryReader(database.ConnectionString)
            .ReadDetailAsync(processor, parentKey, CancellationToken.None);
        Assert.Equal(before, OperationalMetricDependencySnapshotV1Codec.Encode(Assert.IsType<OperationalMetricProjection>(actual)));
        await Publish(); // Identical legacy replay through the unchanged publisher after the schema upgrade.
        Assert.Equal(2, (int)(await Scalar(connection, "SELECT COUNT(*) FROM dbo.OperationalMetricProjection WHERE PublicationFormatVersion=1 AND EvaluationPolicyId IS NULL AND ContainmentResultId IS NULL AND ConflictWitnessId IS NULL;"))!);
        Assert.Equal(0, (int)(await Scalar(connection, "SELECT COUNT(*) FROM dbo.OperationalMetricPublicationResultArchive;"))!);
    }

    private static async Task Prepare(SqlConnection connection)
    {
        await SqlServerMigrationEngine.CreateDefault().ApplyAsync(connection, TimeSpan.FromMinutes(2), CancellationToken.None);
        await using var transaction = connection.BeginTransaction();
        await ApplyProposal(connection, transaction);
        await transaction.CommitAsync();
    }
    private static async Task ApplyProposal(SqlConnection connection, SqlTransaction transaction)
    {
        using var resource = typeof(B1Rc2DesignValidationTests).Assembly.GetManifestResourceStream("B1RC2.ProposedDdl")!;
        using var reader = new StreamReader(resource);
        var text = await reader.ReadToEndAsync();
        // Every command uses this same connection and transaction; no batch commits.
        foreach (var batch in text.Split("\n-- B1 VALIDATION BATCH --\n", StringSplitOptions.None))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = batch;
            await command.ExecuteNonQueryAsync();
        }
    }
    private static async Task Check(SqlConnection connection, string name, string columns, string[] valid, string[] invalid)
    {
        await using var get = connection.CreateCommand();
        get.CommandText = "SELECT definition FROM sys.check_constraints WHERE name=@Name";
        get.Parameters.AddWithValue("@Name", name);
        var definition = (string)(await get.ExecuteScalarAsync())!;
        // Definition comes from the executed proposal; no predicate is hand-recreated.
        await Execute(connection, $"CREATE TABLE #B1CheckProbe ({columns}); ALTER TABLE #B1CheckProbe ADD CHECK {definition};");
        foreach (var row in valid) await Execute(connection, $"INSERT #B1CheckProbe VALUES ({row});");
        foreach (var row in invalid) await Reject(connection, $"INSERT #B1CheckProbe VALUES ({row});", 547);
        await Execute(connection, "DROP TABLE #B1CheckProbe;");
    }
    private static async Task Reject(SqlConnection connection, string sql, int number)
    {
        var error = await Assert.ThrowsAsync<SqlException>(() => Execute(connection, sql));
        Assert.Equal(number, error.Number);
    }
    private static async Task Execute(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
    private static async Task<object?> Scalar(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
    private const string Prefix = "DECLARE @A bigint,@S bigint,@F bigint,@P bigint,@L bigint; SELECT @A=A,@S=S,@F=F,@P=P,@L=L FROM #B1Ids; ";
    private const string WitnessInsert = """
        INSERT dbo.OperationalMetricConflictWitnessArchive
        (WitnessId,WitnessCodecVersion,WitnessPayload,MetricAggregationProcessorRowId,MetricInputStreamRowId,SourceRevisionPosition,ContributionFactRowId,ContributionPosition)
        VALUES (0x01,1,0x01,@A,@S,2,@F,1);
        """;
    private const string ResultInsert = """
        INSERT dbo.OperationalMetricPublicationResultArchive
        (ResultId,OperationalMetricProjectionProcessorRowId,PublicationFormatVersion,MetricAggregationProcessorRowId,MetricInputStreamRowId,
         SourceRevisionPosition,EvaluationKeyHash,EvaluationKeyBinary,EvaluationPolicyId,ResultCodecVersion,ResultPayload,Status,ReasonCode,ConflictWitnessId)
        VALUES (0x01,@P,2,@A,@S,2,0x11,0x01,N'availability-scope-containment/1',2,0x01,1,6,0x01);
        """;
    private const string Seed = """
        DECLARE @M uniqueidentifier=NEWID(),@S bigint,@A bigint,@F bigint,@P bigint,@L bigint;
        INSERT dbo.MetricInputStream (MachineId,StreamKeyBinary,StreamKey) VALUES (@M,0x01,N'b1-test-stream');
        SET @S=SCOPE_IDENTITY();
        INSERT dbo.MetricInputFact
          (MetricInputStreamRowId,Position,FactIdBinary,FactId,MetricInputKey,MetricValue,Unit,StartsAtUtc,EndsAtUtc,CompanyId,SiteId,MachineId,
           ShiftId,ShiftScheduleAssignmentId,OccurrenceSiteId,OccurrenceShiftScheduleAssignmentId,OccurrenceShiftId,OccurrenceStartsAtUtc,OccurrenceEndsAtUtc,ProductionDaySiteId,ProductionBusinessDate)
        VALUES (@S,1,0x01,N'b1-fact',N'duration.running',N'1',N's','2026-10-16T00:00:00+00:00','2026-10-16T00:00:01+00:00',N'c',N's',@M,
           N'shift',N'schedule',N's',N'schedule',N'shift','2026-10-16T00:00:00+00:00','2026-10-16T08:00:00+00:00',N's','2026-10-16');
        SET @F=SCOPE_IDENTITY();
        INSERT dbo.MetricAggregationProcessor (ProcessorKeyBinary,ProcessorKey,MetricInputStreamRowId) VALUES (0x01,N'b1-aggregation',@S);
        SET @A=SCOPE_IDENTITY();
        INSERT dbo.MetricAggregationContribution VALUES (@A,@S,@F,1);
        INSERT dbo.MetricAggregationRevision VALUES (@A,2);
        INSERT dbo.OperationalMetricProjectionProcessor
          (ProcessorKeyBinary,ProcessorKey,MetricAggregationProcessorRowId,MetricInputStreamRowId,PublicationFormatVersion,EvaluationPolicyId)
        VALUES (0x02,N'b1-format2',@A,@S,2,N'availability-scope-containment/1');
        SET @P=SCOPE_IDENTITY();
        INSERT dbo.OperationalMetricProjectionProcessor
          (ProcessorKeyBinary,ProcessorKey,MetricAggregationProcessorRowId,MetricInputStreamRowId)
        VALUES (0x03,N'b1-legacy',@A,@S);
        SET @L=SCOPE_IDENTITY();
        CREATE TABLE #B1Ids (A bigint,S bigint,F bigint,P bigint,L bigint);
        INSERT #B1Ids VALUES (@A,@S,@F,@P,@L);
        INSERT dbo.OperationalMetricConflictWitnessArchive
          (WitnessId,WitnessCodecVersion,WitnessPayload,MetricAggregationProcessorRowId,MetricInputStreamRowId,SourceRevisionPosition,ContributionFactRowId,ContributionPosition)
        VALUES (0x01,1,0x01,@A,@S,2,@F,1);
        """;
}
