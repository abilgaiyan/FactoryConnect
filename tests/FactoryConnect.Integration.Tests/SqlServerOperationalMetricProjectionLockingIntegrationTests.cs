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
    public async Task SerializableProcessorPrefixAcquisitionExcludesExistingProjectionIdentity()
    {
        var published = await CreatePublishedProcessorAsync(seedProjection: true);
        Assert.NotNull(published.SeededEvaluationKeyHash);

        await using var ownerConnection = _fixture.CreateConnection();
        await ownerConnection.OpenAsync();
        await using var ownerTransaction = (SqlTransaction)await ownerConnection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            CancellationToken.None);

        await AcquireProcessorPrefixAsync(
            ownerConnection,
            ownerTransaction,
            published.ProjectionProcessorRowId,
            CancellationToken.None);

        await using var contenderConnection = _fixture.CreateConnection();
        await contenderConnection.OpenAsync();
        var exception = await Assert.ThrowsAsync<SqlException>(() =>
            ProbeProjectionIdentityWithZeroWaitAsync(
                contenderConnection,
                published.ProjectionProcessorRowId,
                published.SeededEvaluationKeyHash!,
                CancellationToken.None));

        Assert.Equal(LockRequestTimeoutNumber, exception.Number);

        await ownerTransaction.RollbackAsync(CancellationToken.None);

        Assert.True(await ProbeProjectionIdentityWithZeroWaitAsync(
            contenderConnection,
            published.ProjectionProcessorRowId,
            published.SeededEvaluationKeyHash!,
            CancellationToken.None));
    }

    [Fact]
    public async Task SerializableProcessorPrefixAcquisitionExcludesMissingProjectionIdentityRange()
    {
        var published = await CreatePublishedProcessorAsync(seedProjection: false);
        var missingKey = CreateShiftKey(published.MachineId, "missing-range", "1");
        var missingHash = OperationalMetricEvaluationKeyV1Codec.ComputeHash(
            OperationalMetricEvaluationKeyV1Codec.Encode(missingKey));

        await using var ownerConnection = _fixture.CreateConnection();
        await ownerConnection.OpenAsync();
        await using var ownerTransaction = (SqlTransaction)await ownerConnection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            CancellationToken.None);

        await AcquireProcessorPrefixAsync(
            ownerConnection,
            ownerTransaction,
            published.ProjectionProcessorRowId,
            CancellationToken.None);

        await using var contenderConnection = _fixture.CreateConnection();
        await contenderConnection.OpenAsync();
        var exception = await Assert.ThrowsAsync<SqlException>(() =>
            ProbeProjectionIdentityWithZeroWaitAsync(
                contenderConnection,
                published.ProjectionProcessorRowId,
                missingHash,
                CancellationToken.None));

        Assert.Equal(LockRequestTimeoutNumber, exception.Number);

        await ownerTransaction.RollbackAsync(CancellationToken.None);

        Assert.False(await ProbeProjectionIdentityWithZeroWaitAsync(
            contenderConnection,
            published.ProjectionProcessorRowId,
            missingHash,
            CancellationToken.None));
    }

    [Fact]
    public async Task SerializableProcessorPrefixAcquisitionRejectsCompetingSameProcessorProjectionStage()
    {
        var published = await CreatePublishedProcessorAsync(seedProjection: true);

        await using var ownerConnection = _fixture.CreateConnection();
        await ownerConnection.OpenAsync();
        await using var ownerTransaction = (SqlTransaction)await ownerConnection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            CancellationToken.None);

        await AcquireProcessorPrefixAsync(
            ownerConnection,
            ownerTransaction,
            published.ProjectionProcessorRowId,
            CancellationToken.None);

        await using var contenderConnection = _fixture.CreateConnection();
        await contenderConnection.OpenAsync();
        var exception = await Assert.ThrowsAsync<SqlException>(() =>
            AcquireProcessorPrefixWithZeroWaitAsync(
                contenderConnection,
                published.ProjectionProcessorRowId,
                CancellationToken.None));

        Assert.Equal(LockRequestTimeoutNumber, exception.Number);

        await ownerTransaction.RollbackAsync(CancellationToken.None);

        await AcquireProcessorPrefixWithZeroWaitAsync(
            contenderConnection,
            published.ProjectionProcessorRowId,
            CancellationToken.None);
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

        byte[]? seededHash = null;
        if (seedProjection)
        {
            var key = CreateShiftKey(source.MachineId, "availability", "1");
            seededHash = await SeedProjectionAndManifestAsync(
                header.ProjectionProcessorRowId,
                source.Checkpoint,
                key);
        }

        return new PublishedProcessorFixture(
            source.MachineId,
            header.ProjectionProcessorRowId,
            seededHash);
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

    private static async Task AcquireProcessorPrefixAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long projectionProcessorRowId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EvaluationKeyHash
            FROM dbo.OperationalMetricProjection WITH
                (UPDLOCK, HOLDLOCK, INDEX(UQ_OperationalMetricProjection_LogicalHash))
            WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId
            ORDER BY EvaluationKeyHash;
            """;
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value =
            projectionProcessorRowId;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
        }
    }

    private static async Task AcquireProcessorPrefixWithZeroWaitAsync(
        SqlConnection connection,
        long projectionProcessorRowId,
        CancellationToken cancellationToken)
    {
        await SetZeroLockTimeoutAsync(connection, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            await AcquireProcessorPrefixAsync(
                connection,
                transaction,
                projectionProcessorRowId,
                cancellationToken);
            await transaction.RollbackAsync(CancellationToken.None);
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

    private static async Task<bool> ProbeProjectionIdentityWithZeroWaitAsync(
        SqlConnection connection,
        long projectionProcessorRowId,
        byte[] evaluationKeyHash,
        CancellationToken cancellationToken)
    {
        await SetZeroLockTimeoutAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
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

        return await command.ExecuteScalarAsync(cancellationToken) is not null;
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

    private sealed record PublishedProcessorFixture(
        MachineId MachineId,
        long ProjectionProcessorRowId,
        byte[]? SeededEvaluationKeyHash);
}
