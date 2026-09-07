using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerOperationalMetricProjectionLockingIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private const int LockRequestTimeoutNumber = 1222;
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerOperationalMetricProjectionLockingIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ProductionPrepareExcludesExistingProjectionIdentityUntilRollback()
    {
        var published = await CreatePublishedProcessorAsync(seedProjection: true);
        Assert.NotNull(published.SeededProjection);

        var held = HoldProductionPreparationAsync(
            published,
            [published.SeededProjection!]);
        await held.PreparationComplete.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await using var contenderConnection = _fixture.CreateConnection();
        await contenderConnection.OpenAsync();
        var exception = await Assert.ThrowsAsync<SqlException>(() =>
            ProbeProjectionIdentityWithZeroWaitAsync(
                contenderConnection,
                published.ProjectionProcessorRowId,
                published.SeededProjection!.EvaluationKeyHash,
                CancellationToken.None));

        Assert.Equal(LockRequestTimeoutNumber, exception.Number);

        held.Release.TrySetResult(true);
        await Assert.ThrowsAsync<PreparationInspectionCompleteException>(() => held.WriterTask);

        Assert.True(await ProbeProjectionIdentityWithZeroWaitAsync(
            contenderConnection,
            published.ProjectionProcessorRowId,
            published.SeededProjection!.EvaluationKeyHash,
            CancellationToken.None));
    }

    [Fact]
    public async Task ProductionPrepareExcludesMissingIdentityInEmptyProcessorRangeUntilRollback()
    {
        var published = await CreatePublishedProcessorAsync(seedProjection: false);
        var missingKey = CreateShiftKey(published.MachineId, "missing-empty-range", "1");
        var missingHash = OperationalMetricEvaluationKeyV1Codec.ComputeHash(
            OperationalMetricEvaluationKeyV1Codec.Encode(missingKey));

        var held = HoldProductionPreparationAsync(published, []);
        await held.PreparationComplete.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await using var contenderConnection = _fixture.CreateConnection();
        await contenderConnection.OpenAsync();
        var exception = await Assert.ThrowsAsync<SqlException>(() =>
            ProbeProjectionIdentityWithZeroWaitAsync(
                contenderConnection,
                published.ProjectionProcessorRowId,
                missingHash,
                CancellationToken.None));

        Assert.Equal(LockRequestTimeoutNumber, exception.Number);

        held.Release.TrySetResult(true);
        await Assert.ThrowsAsync<PreparationInspectionCompleteException>(() => held.WriterTask);

        Assert.False(await ProbeProjectionIdentityWithZeroWaitAsync(
            contenderConnection,
            published.ProjectionProcessorRowId,
            missingHash,
            CancellationToken.None));
    }

    [Fact]
    public async Task ProductionPrepareExcludesMissingHashInsideNonemptyProcessorRangeUntilRollback()
    {
        var published = await CreatePublishedProcessorAsync(seedProjection: true);
        Assert.NotNull(published.SeededProjection);
        var missingKey = CreateShiftKey(published.MachineId, "missing-nonempty-range", "1");
        var missingHash = OperationalMetricEvaluationKeyV1Codec.ComputeHash(
            OperationalMetricEvaluationKeyV1Codec.Encode(missingKey));
        Assert.False(missingHash.AsSpan().SequenceEqual(published.SeededProjection!.EvaluationKeyHash));

        var held = HoldProductionPreparationAsync(
            published,
            [published.SeededProjection!]);
        await held.PreparationComplete.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await using var contenderConnection = _fixture.CreateConnection();
        await contenderConnection.OpenAsync();
        var exception = await Assert.ThrowsAsync<SqlException>(() =>
            ProbeProjectionIdentityWithZeroWaitAsync(
                contenderConnection,
                published.ProjectionProcessorRowId,
                missingHash,
                CancellationToken.None));

        Assert.Equal(LockRequestTimeoutNumber, exception.Number);

        held.Release.TrySetResult(true);
        await Assert.ThrowsAsync<PreparationInspectionCompleteException>(() => held.WriterTask);

        Assert.False(await ProbeProjectionIdentityWithZeroWaitAsync(
            contenderConnection,
            published.ProjectionProcessorRowId,
            missingHash,
            CancellationToken.None));
    }

    [Fact]
    public async Task DistinctProcessorsCompleteProductionPreparationIndependently()
    {
        var processorA = await CreatePublishedProcessorAsync(seedProjection: true);
        var processorB = await CreatePublishedProcessorAsync(seedProjection: true);
        Assert.NotNull(processorA.SeededProjection);
        Assert.NotNull(processorB.SeededProjection);

        var heldA = HoldProductionPreparationAsync(
            processorA,
            [processorA.SeededProjection!]);
        await heldA.PreparationComplete.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var transactionB = new SqlServerOperationalMetricProjectionCommitTransaction(
            _fixture.ConnectionString);
        var commitB = CreateReplayCommit(
            processorB,
            [processorB.SeededProjection!.Projection]);
        var preparationBCompleted = false;

        var writerB = transactionB.ExecuteAsync(
            commitB,
            async (context, cancellationToken) =>
            {
                var plan = await SqlServerOperationalMetricProjectionRows.PrepareAsync(
                    context,
                    commitB,
                    cancellationToken);
                Assert.Single(plan.ProposedRows);
                Assert.NotNull(plan.ProposedRows[0].ExistingProjectionRowId);
                preparationBCompleted = true;
                throw new PreparationInspectionCompleteException();
            },
            CancellationToken.None);

        await Assert.ThrowsAsync<PreparationInspectionCompleteException>(
            () => writerB.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(preparationBCompleted);
        Assert.False(heldA.Release.Task.IsCompleted);

        heldA.Release.TrySetResult(true);
        await Assert.ThrowsAsync<PreparationInspectionCompleteException>(() => heldA.WriterTask);
    }

    private HeldPreparation HoldProductionPreparationAsync(
        PublishedProcessorFixture published,
        IReadOnlyList<SeededProjectionFixture> proposed)
    {
        var preparationComplete = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var transaction = new SqlServerOperationalMetricProjectionCommitTransaction(
            _fixture.ConnectionString);
        var commit = CreateReplayCommit(
            published,
            proposed.Select(static item => item.Projection).ToArray());

        var writerTask = transaction.ExecuteAsync(
            commit,
            async (context, cancellationToken) =>
            {
                var plan = await SqlServerOperationalMetricProjectionRows.PrepareAsync(
                    context,
                    commit,
                    cancellationToken);
                Assert.Equal(proposed.Count, plan.ProposedRows.Count);
                preparationComplete.TrySetResult(true);
                await release.Task.WaitAsync(cancellationToken);
                throw new PreparationInspectionCompleteException();
            },
            CancellationToken.None);

        return new HeldPreparation(preparationComplete, release, writerTask);
    }

    private static OperationalMetricProjectionCommit CreateReplayCommit(
        PublishedProcessorFixture published,
        IReadOnlyList<OperationalMetricProjection> projections)
    {
        var proposedCheckpoint = new OperationalMetricProjectionCheckpoint(
            published.ProcessorId,
            published.SourceCheckpoint,
            new OperationalMetricProjectionBatchManifest(
                projections.Select(static projection => projection.Key)));
        return new OperationalMetricProjectionCommit(
            published.ProcessorId,
            expectedCheckpoint: null,
            proposedCheckpoint,
            projections);
    }

    private async Task<PublishedProcessorFixture> CreatePublishedProcessorAsync(bool seedProjection)
    {
        var source = await CreateSourceAsync();
        var processorId = new OperationalMetricProjectionProcessorId(
            $"projection-lock-{Guid.NewGuid():N}");
        var proposedCheckpoint = new OperationalMetricProjectionCheckpoint(
            processorId,
            source.Checkpoint);
        var commit = new OperationalMetricProjectionCommit(
            processorId,
            expectedCheckpoint: null,
            proposedCheckpoint,
            []);
        var transaction = new SqlServerOperationalMetricProjectionCommitTransaction(
            _fixture.ConnectionString);

        await transaction.ExecuteAsync(
            commit,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        var header = await transaction.ReadCheckpointHeaderAsync(
            processorId,
            CancellationToken.None);
        Assert.NotNull(header);

        SeededProjectionFixture? seededProjection = null;
        if (seedProjection)
        {
            var key = CreateShiftKey(source.MachineId, "availability", "1");
            var projection = new OperationalMetricProjection(
                processorId,
                key,
                OperationalMetricEvaluationStatus.Calculated,
                0.5m,
                "ratio",
                reasonCode: null,
                reasonOperandName: null,
                source.Checkpoint);
            var hash = await SeedProjectionAndManifestAsync(
                header.ProjectionProcessorRowId,
                source.Checkpoint,
                key);
            seededProjection = new SeededProjectionFixture(projection, hash);
        }

        return new PublishedProcessorFixture(
            processorId,
            source.MachineId,
            source.Checkpoint,
            header.ProjectionProcessorRowId,
            seededProjection);
    }

    private async Task<byte[]> SeedProjectionAndManifestAsync(
        long projectionProcessorRowId,
        MetricAggregationCheckpoint sourceRevision,
        OperationalMetricEvaluationKey key)
    {
        var binary = OperationalMetricEvaluationKeyV1Codec.Encode(key);
        var hash = OperationalMetricEvaluationKeyV1Codec.ComputeHash(binary);
        var shift = Assert.IsType<OperationalMetricPeriodId.Shift>(key.PeriodId);

        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            CancellationToken.None);

        await using var insertProjection = connection.CreateCommand();
        insertProjection.Transaction = transaction;
        insertProjection.CommandText = """
            INSERT INTO dbo.OperationalMetricProjection
            (
                OperationalMetricProjectionProcessorRowId,
                EvaluationKeyCodecVersion,
                EvaluationKeyHash,
                EvaluationKeyBinary,
                MachineId,
                PeriodKind,
                PeriodSiteId,
                PeriodSiteOrderKey,
                ShiftScheduleAssignmentId,
                ShiftScheduleAssignmentOrderKey,
                ShiftId,
                ShiftOrderKey,
                ShiftStartsAtUtc,
                ShiftEndsAtUtc,
                ProductionBusinessDate,
                ProductionOrderPresent,
                ProductionOrderId,
                ProductionOrderOrderKey,
                OperationPresent,
                OperationId,
                OperationOrderKey,
                PartPresent,
                PartId,
                PartOrderKey,
                OperatorPresent,
                OperatorId,
                OperatorOrderKey,
                MetricKey,
                MetricKeyOrderKey,
                DefinitionVersion,
                DefinitionVersionOrderKey,
                Status,
                MetricValue,
                Unit,
                ReasonCode,
                ReasonOperandName,
                SourceRevisionPosition
            )
            OUTPUT INSERTED.OperationalMetricProjectionRowId
            VALUES
            (
                @ProcessorRowId,
                @CodecVersion,
                @Hash,
                @Binary,
                @MachineId,
                1,
                @PeriodSiteId,
                @PeriodSiteOrderKey,
                @ScheduleId,
                @ScheduleOrderKey,
                @ShiftId,
                @ShiftOrderKey,
                @StartsAtUtc,
                @EndsAtUtc,
                NULL,
                0, NULL, NULL,
                0, NULL, NULL,
                0, NULL, NULL,
                0, NULL, NULL,
                @MetricKey,
                @MetricKeyOrderKey,
                @DefinitionVersion,
                @DefinitionVersionOrderKey,
                0,
                N'0.5',
                N'ratio',
                NULL,
                NULL,
                @SourceRevisionPosition
            );
            """;
        insertProjection.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value =
            projectionProcessorRowId;
        insertProjection.Parameters.Add("@CodecVersion", SqlDbType.SmallInt).Value =
            OperationalMetricEvaluationKeyV1Codec.CodecVersion;
        insertProjection.Parameters.Add("@Hash", SqlDbType.Binary, 32).Value = hash;
        insertProjection.Parameters.Add("@Binary", SqlDbType.VarBinary, -1).Value = binary;
        insertProjection.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value =
            key.MachineId.Value;
        AddString(insertProjection, "@PeriodSiteId", shift.ShiftOccurrenceId.SiteId.Value);
        AddOrderKey(insertProjection, "@PeriodSiteOrderKey", shift.ShiftOccurrenceId.SiteId.Value);
        AddString(insertProjection, "@ScheduleId", shift.ShiftOccurrenceId.ShiftScheduleAssignmentId.Value);
        AddOrderKey(insertProjection, "@ScheduleOrderKey", shift.ShiftOccurrenceId.ShiftScheduleAssignmentId.Value);
        AddString(insertProjection, "@ShiftId", shift.ShiftOccurrenceId.ShiftId.Value);
        AddOrderKey(insertProjection, "@ShiftOrderKey", shift.ShiftOccurrenceId.ShiftId.Value);
        insertProjection.Parameters.Add("@StartsAtUtc", SqlDbType.DateTimeOffset).Value =
            shift.ShiftOccurrenceId.StartsAtUtc;
        insertProjection.Parameters.Add("@EndsAtUtc", SqlDbType.DateTimeOffset).Value =
            shift.ShiftOccurrenceId.EndsAtUtc;
        AddString(insertProjection, "@MetricKey", key.DefinitionId.MetricKey);
        AddOrderKey(insertProjection, "@MetricKeyOrderKey", key.DefinitionId.MetricKey);
        AddString(insertProjection, "@DefinitionVersion", key.DefinitionId.Version);
        AddOrderKey(insertProjection, "@DefinitionVersionOrderKey", key.DefinitionId.Version);
        insertProjection.Parameters.Add(
            SqlServerUInt64.CreateParameter(
                "@SourceRevisionPosition",
                sourceRevision.Position.Value));

        var projectionRowId = Convert.ToInt64(
            await insertProjection.ExecuteScalarAsync(CancellationToken.None),
            System.Globalization.CultureInfo.InvariantCulture);

        await using var insertManifest = connection.CreateCommand();
        insertManifest.Transaction = transaction;
        insertManifest.CommandText = """
            INSERT INTO dbo.OperationalMetricProjectionManifest
                (OperationalMetricProjectionProcessorRowId, OperationalMetricProjectionRowId)
            VALUES (@ProcessorRowId, @ProjectionRowId);
            """;
        insertManifest.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value =
            projectionProcessorRowId;
        insertManifest.Parameters.Add("@ProjectionRowId", SqlDbType.BigInt).Value =
            projectionRowId;
        Assert.Equal(
            1,
            await insertManifest.ExecuteNonQueryAsync(CancellationToken.None));

        await transaction.CommitAsync(CancellationToken.None);
        return hash;
    }

    private static async Task<bool> ProbeProjectionIdentityWithZeroWaitAsync(
        SqlConnection connection,
        long projectionProcessorRowId,
        byte[] evaluationKeyHash,
        CancellationToken cancellationToken)
    {
        await SetZeroLockTimeoutAsync(connection, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT OperationalMetricProjectionRowId
                FROM dbo.OperationalMetricProjection WITH
                    (UPDLOCK, HOLDLOCK, INDEX(UQ_OperationalMetricProjection_LogicalHash))
                WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId
                  AND EvaluationKeyHash = @Hash;
                """;
            command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value =
                projectionProcessorRowId;
            command.Parameters.Add("@Hash", SqlDbType.Binary, 32).Value =
                evaluationKeyHash;

            var result = await command.ExecuteScalarAsync(cancellationToken);
            await transaction.RollbackAsync(CancellationToken.None);
            return result is not null;
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch
            {
            }

            throw;
        }
    }

    private static async Task SetZeroLockTimeoutAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SET LOCK_TIMEOUT 0;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static OperationalMetricEvaluationKey CreateShiftKey(
        MachineId machineId,
        string metricKey,
        string version)
    {
        var period = new OperationalMetricPeriodId.Shift(
            new ShiftOccurrenceId(
                new SiteId("SITE-1"),
                new ShiftScheduleAssignmentId("SCHEDULE-A"),
                new ShiftId("SHIFT-A"),
                new DateTimeOffset(2026, 9, 7, 6, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 9, 7, 14, 0, 0, TimeSpan.Zero)));
        return new OperationalMetricEvaluationKey(
            machineId,
            period,
            new OperationalMetricDefinitionId(metricKey, version),
            OperationalMetricEvaluationContextKey.Unpartitioned);
    }

    private async Task<SourceFixture> CreateSourceAsync()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var fact = await inputStore.AppendAsync(
            CreateAppend(machineId, $"projection-lock-source-{Guid.NewGuid():N}"),
            CancellationToken.None);
        var aggregationProcessorId = new MetricAggregationProcessorId(
            $"projection-lock-source-{Guid.NewGuid():N}");
        var checkpoint = new MetricAggregationCheckpoint(
            aggregationProcessorId,
            fact.StreamId,
            fact.Position);
        var aggregationStore = new SqlServerMetricAggregationStore(_fixture.ConnectionString);
        await aggregationStore.CommitAsync(
            new MetricAggregationCommit(
                aggregationProcessorId,
                expectedCheckpoint: null,
                checkpoint,
                []),
            CancellationToken.None);

        return new SourceFixture(machineId, checkpoint);
    }

    private static DurableMetricInputAppend CreateAppend(
        MachineId machineId,
        string factId)
    {
        var siteId = new SiteId("SITE-1");
        var shiftId = new ShiftId("SHIFT-A");
        var scheduleId = new ShiftScheduleAssignmentId("SCHEDULE-A");
        var occurrenceStart = new DateTimeOffset(
            2026,
            9,
            7,
            6,
            0,
            0,
            TimeSpan.Zero);
        var fact = new DurableMetricInputFact
        {
            Id = new MetricInputFactId(factId),
            Key = "running-duration",
            Value = 1m,
            Unit = "seconds",
            StartsAtUtc = occurrenceStart,
            EndsAtUtc = occurrenceStart.AddMinutes(1),
            CompanyId = new CompanyId("COMP-1"),
            SiteId = siteId,
            ProductionLineId = new ProductionLineId("LINE-1"),
            MachineId = machineId,
            ShiftId = shiftId,
            ShiftScheduleAssignmentId = scheduleId,
        };
        return new DurableMetricInputAppend(
            MetricInputStreamId.ForMachine(machineId),
            fact,
            new ShiftOccurrenceId(
                siteId,
                scheduleId,
                shiftId,
                occurrenceStart,
                occurrenceStart.AddHours(8)),
            new ProductionDayId(
                siteId,
                DateOnly.FromDateTime(occurrenceStart.UtcDateTime)));
    }

    private static void AddString(SqlCommand command, string name, string value) =>
        command.Parameters.Add(name, SqlDbType.NVarChar, 256).Value = value;

    private static void AddOrderKey(SqlCommand command, string name, string value) =>
        command.Parameters.Add(
            name,
            SqlDbType.VarBinary,
            StringOrderKeyV2Codec.MaximumEncodedLength).Value =
            StringOrderKeyV2Codec.Encode(value);

    private sealed record SourceFixture(
        MachineId MachineId,
        MetricAggregationCheckpoint Checkpoint);

    private sealed record SeededProjectionFixture(
        OperationalMetricProjection Projection,
        byte[] EvaluationKeyHash);

    private sealed record PublishedProcessorFixture(
        OperationalMetricProjectionProcessorId ProcessorId,
        MachineId MachineId,
        MetricAggregationCheckpoint SourceCheckpoint,
        long ProjectionProcessorRowId,
        SeededProjectionFixture? SeededProjection);

    private sealed record HeldPreparation(
        TaskCompletionSource<bool> PreparationComplete,
        TaskCompletionSource<bool> Release,
        Task WriterTask);

    private sealed class PreparationInspectionCompleteException : Exception
    {
    }
}
