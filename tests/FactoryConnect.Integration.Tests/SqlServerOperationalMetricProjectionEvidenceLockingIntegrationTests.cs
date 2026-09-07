using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerOperationalMetricProjectionEvidenceLockingIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private const int LockRequestTimeoutNumber = 1222;
    private const string EvidenceOrdinalIndexName =
        "UQ_OperationalMetricProjectionEvidence_Ordinal";

    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerOperationalMetricProjectionEvidenceLockingIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ExistingEvidenceIdentityIsExcludedUntilOwnerRollback()
    {
        var published = await CreatePublishedAsync(includeEvidence: true);
        var held = HoldEvidenceBoundaryAsync(published);

        try
        {
            await held.BoundaryComplete.Task.WaitAsync(TimeSpan.FromSeconds(10));

            await using var contenderConnection = _fixture.CreateConnection();
            await contenderConnection.OpenAsync();
            var exception = await Assert.ThrowsAsync<SqlException>(() =>
                ProbeEvidenceIdentityWithZeroWaitAsync(
                    contenderConnection,
                    published.ProjectionRowId,
                    CancellationToken.None));

            Assert.Equal(LockRequestTimeoutNumber, exception.Number);

            held.Release.TrySetResult(true);
            await Assert.ThrowsAsync<InspectionCompleteException>(() => held.WriterTask);
        }
        finally
        {
            await CleanupHeldWriterAsync(held);
        }
    }

    [Fact]
    public async Task EmptyEvidencePrefixBlocksPhantomInsertOnFrozenOrdinalIndex()
    {
        var published = await CreatePublishedAsync(includeEvidence: false);
        var held = HoldEvidenceBoundaryAsync(published);
        Task? contender = null;

        try
        {
            await held.BoundaryComplete.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var ownerSessionId = await held.OwnerSessionId.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var contenderSessionId = new TaskCompletionSource<int>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            contender = InsertEvidenceThenRollbackAsync(
                published,
                contenderSessionId,
                CancellationToken.None);

            var contenderSpid = await contenderSessionId.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var wait = await WaitForEvidenceOrdinalBlockingAsync(
                ownerSessionId,
                contenderSpid,
                TimeSpan.FromSeconds(10),
                CancellationToken.None);

            Assert.Equal(ownerSessionId, wait.BlockingSessionId);
            Assert.StartsWith("LCK_M_", wait.WaitType, StringComparison.Ordinal);
            Assert.StartsWith("KEY:", wait.WaitResource, StringComparison.Ordinal);
            Assert.Equal(EvidenceOrdinalIndexName, wait.IndexName);

            held.Release.TrySetResult(true);
            await Assert.ThrowsAsync<InspectionCompleteException>(() => held.WriterTask);
            await contender.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(0, await CountEvidenceRowsAsync(published.ProcessorId));
        }
        finally
        {
            await CleanupHeldWriterAsync(held);

            if (contender is not null && !contender.IsCompleted)
            {
                try
                {
                    await contender.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch
                {
                    // Cleanup only; preserve the primary assertion failure.
                }
            }
        }
    }

    [Fact]
    public async Task EvidenceLockIsReleasedAfterOwnerRollback()
    {
        var published = await CreatePublishedAsync(includeEvidence: true);
        var held = HoldEvidenceBoundaryAsync(published);

        try
        {
            await held.BoundaryComplete.Task.WaitAsync(TimeSpan.FromSeconds(10));

            await using var contenderConnection = _fixture.CreateConnection();
            await contenderConnection.OpenAsync();

            var blocked = await Assert.ThrowsAsync<SqlException>(() =>
                ProbeEvidenceIdentityWithZeroWaitAsync(
                    contenderConnection,
                    published.ProjectionRowId,
                    CancellationToken.None));
            Assert.Equal(LockRequestTimeoutNumber, blocked.Number);

            held.Release.TrySetResult(true);
            await Assert.ThrowsAsync<InspectionCompleteException>(() => held.WriterTask);

            Assert.True(await ProbeEvidenceIdentityWithZeroWaitAsync(
                contenderConnection,
                published.ProjectionRowId,
                CancellationToken.None));
        }
        finally
        {
            await CleanupHeldWriterAsync(held);
        }
    }

    [Fact]
    public async Task EvidenceLockPreparationRollbackLeavesPublicationAndCheckpointUnchanged()
    {
        var published = await CreatePublishedAsync(includeEvidence: true);
        var transaction = new SqlServerOperationalMetricProjectionCommitTransaction(
            _fixture.ConnectionString);
        var before = await transaction.ReadCheckpointHeaderAsync(
            published.ProcessorId,
            CancellationToken.None);
        Assert.NotNull(before);

        var held = HoldEvidenceBoundaryAsync(published);

        try
        {
            await held.BoundaryComplete.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, await CountProjectionRowsAsync(published.ProcessorId));
            Assert.Equal(1, await CountManifestRowsAsync(published.ProcessorId));
            Assert.Equal(1, await CountEvidenceRowsAsync(published.ProcessorId));

            held.Release.TrySetResult(true);
            await Assert.ThrowsAsync<InspectionCompleteException>(() => held.WriterTask);

            var after = await transaction.ReadCheckpointHeaderAsync(
                published.ProcessorId,
                CancellationToken.None);
            Assert.NotNull(after);
            Assert.Equal(before.Position, after.Position);
            Assert.Equal(1, await CountProjectionRowsAsync(published.ProcessorId));
            Assert.Equal(1, await CountManifestRowsAsync(published.ProcessorId));
            Assert.Equal(1, await CountEvidenceRowsAsync(published.ProcessorId));
        }
        finally
        {
            await CleanupHeldWriterAsync(held);
        }
    }

    private HeldWriter HoldEvidenceBoundaryAsync(PublishedFixture published)
    {
        var boundaryComplete = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var ownerSessionId = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var transaction = new SqlServerOperationalMetricProjectionCommitTransaction(
            _fixture.ConnectionString);
        var commit = CreateReplayCommit(published);

        var writerTask = transaction.ExecuteAsync(
            commit,
            async (context, cancellationToken) =>
            {
                var prepared = await SqlServerOperationalMetricProjectionPublicationLocks.PrepareAsync(
                    context,
                    commit,
                    cancellationToken);

                Assert.Single(prepared.ProjectionPlan.ProposedRows);
                Assert.Equal(
                    published.ProjectionRowId,
                    prepared.ProjectionPlan.ProposedRows[0].ExistingProjectionRowId);
                Assert.Equal(
                    [published.ProjectionRowId],
                    prepared.Locks.ManifestProjectionRowIds);

                ownerSessionId.TrySetResult(
                    await ReadSessionIdAsync(context, cancellationToken));
                boundaryComplete.TrySetResult(true);
                await release.Task.WaitAsync(cancellationToken);
                throw new InspectionCompleteException();
            },
            CancellationToken.None);

        return new HeldWriter(
            boundaryComplete,
            ownerSessionId,
            release,
            writerTask);
    }

    private async Task<PublishedFixture> CreatePublishedAsync(bool includeEvidence)
    {
        var source = await CreateSourceAsync();
        var processorId = new OperationalMetricProjectionProcessorId(
            $"evidence-lock-{Guid.NewGuid():N}");
        var transaction = new SqlServerOperationalMetricProjectionCommitTransaction(
            _fixture.ConnectionString);
        var initial = new OperationalMetricProjectionCommit(
            processorId,
            expectedCheckpoint: null,
            new OperationalMetricProjectionCheckpoint(processorId, source.Checkpoint),
            []);

        await transaction.ExecuteAsync(
            initial,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        var header = await transaction.ReadCheckpointHeaderAsync(
            processorId,
            CancellationToken.None);
        Assert.NotNull(header);

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
        var projectionRowId = await SeedProjectionAsync(
            header.ProjectionProcessorRowId,
            source.Checkpoint,
            key,
            includeEvidence);

        return new PublishedFixture(
            processorId,
            source,
            projection,
            header.ProjectionProcessorRowId,
            projectionRowId);
    }

    private async Task<long> SeedProjectionAsync(
        long processorRowId,
        MetricAggregationCheckpoint sourceRevision,
        OperationalMetricEvaluationKey key,
        bool includeEvidence)
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
                1,
                @Hash,
                @Binary,
                @MachineId,
                1,
                @SiteId,
                @SiteOrderKey,
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
                @MetricOrderKey,
                @Version,
                @VersionOrderKey,
                0,
                N'0.5',
                N'ratio',
                NULL,
                NULL,
                @Position
            );
            """;
        insertProjection.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
        insertProjection.Parameters.Add("@Hash", SqlDbType.Binary, 32).Value = hash;
        insertProjection.Parameters.Add("@Binary", SqlDbType.VarBinary, -1).Value = binary;
        insertProjection.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value = key.MachineId.Value;
        AddString(insertProjection, "@SiteId", shift.ShiftOccurrenceId.SiteId.Value);
        AddOrderKey(insertProjection, "@SiteOrderKey", shift.ShiftOccurrenceId.SiteId.Value);
        AddString(
            insertProjection,
            "@ScheduleId",
            shift.ShiftOccurrenceId.ShiftScheduleAssignmentId.Value);
        AddOrderKey(
            insertProjection,
            "@ScheduleOrderKey",
            shift.ShiftOccurrenceId.ShiftScheduleAssignmentId.Value);
        AddString(insertProjection, "@ShiftId", shift.ShiftOccurrenceId.ShiftId.Value);
        AddOrderKey(insertProjection, "@ShiftOrderKey", shift.ShiftOccurrenceId.ShiftId.Value);
        insertProjection.Parameters.Add("@StartsAtUtc", SqlDbType.DateTimeOffset).Value =
            shift.ShiftOccurrenceId.StartsAtUtc;
        insertProjection.Parameters.Add("@EndsAtUtc", SqlDbType.DateTimeOffset).Value =
            shift.ShiftOccurrenceId.EndsAtUtc;
        AddString(insertProjection, "@MetricKey", key.DefinitionId.MetricKey);
        AddOrderKey(insertProjection, "@MetricOrderKey", key.DefinitionId.MetricKey);
        AddString(insertProjection, "@Version", key.DefinitionId.Version);
        AddOrderKey(insertProjection, "@VersionOrderKey", key.DefinitionId.Version);
        insertProjection.Parameters.Add(
            SqlServerUInt64.CreateParameter("@Position", sourceRevision.Position.Value));

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
        insertManifest.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
        insertManifest.Parameters.Add("@ProjectionRowId", SqlDbType.BigInt).Value = projectionRowId;
        Assert.Equal(1, await insertManifest.ExecuteNonQueryAsync(CancellationToken.None));

        if (includeEvidence)
        {
            await InsertEvidenceAsync(
                connection,
                transaction,
                projectionRowId,
                shift.ShiftOccurrenceId.StartsAtUtc,
                CancellationToken.None);
        }

        await transaction.CommitAsync(CancellationToken.None);
        return projectionRowId;
    }

    private async Task InsertEvidenceThenRollbackAsync(
        PublishedFixture published,
        TaskCompletionSource<int> sessionId,
        CancellationToken cancellationToken)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        try
        {
            sessionId.TrySetResult(
                await ReadSessionIdAsync(connection, transaction, cancellationToken));
            var shift = Assert.IsType<OperationalMetricPeriodId.Shift>(
                published.Projection.Key.PeriodId);
            await InsertEvidenceAsync(
                connection,
                transaction,
                published.ProjectionRowId,
                shift.ShiftOccurrenceId.StartsAtUtc,
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
                // Preserve the primary SQL/cancellation failure.
            }

            throw;
        }
    }

    private static async Task InsertEvidenceAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long projectionRowId,
        DateTimeOffset firstInputTimestamp,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO dbo.OperationalMetricProjectionEvidence
            (
                OperationalMetricProjectionRowId,
                EvidenceKind,
                EvidenceOrdinal,
                OperandName,
                OperandNameOrderKey,
                ComponentKey,
                MetricDimension,
                ComponentValue,
                ComponentUnit,
                InputCount,
                FirstInputTimestamp,
                LastInputTimestamp
            )
            VALUES
            (
                @ProjectionRowId,
                1,
                0,
                N'component',
                @OperandOrderKey,
                N'running-duration',
                0,
                N'1',
                N'seconds',
                1,
                @First,
                @Last
            );
            """;
        command.Parameters.Add("@ProjectionRowId", SqlDbType.BigInt).Value = projectionRowId;
        command.Parameters.Add("@OperandOrderKey", SqlDbType.VarBinary, 769).Value =
            StringOrderKeyV2Codec.Encode("component");
        command.Parameters.Add("@First", SqlDbType.DateTimeOffset).Value = firstInputTimestamp;
        command.Parameters.Add("@Last", SqlDbType.DateTimeOffset).Value =
            firstInputTimestamp.AddMinutes(1);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
    }

    private static async Task<bool> ProbeEvidenceIdentityWithZeroWaitAsync(
        SqlConnection connection,
        long projectionRowId,
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
                SELECT OperationalMetricProjectionEvidenceRowId
                FROM dbo.OperationalMetricProjectionEvidence WITH
                    (UPDLOCK, HOLDLOCK, INDEX(UQ_OperationalMetricProjectionEvidence_Ordinal))
                WHERE OperationalMetricProjectionRowId = @ProjectionRowId
                  AND EvidenceKind = 1
                  AND EvidenceOrdinal = 0;
                """;
            command.Parameters.Add("@ProjectionRowId", SqlDbType.BigInt).Value = projectionRowId;
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is not null && result is not DBNull;
        }
        finally
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch
            {
                // Preserve the primary lock timeout/cancellation failure.
            }
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

    private async Task<BlockingWaitSnapshot> WaitForEvidenceOrdinalBlockingAsync(
        int ownerSessionId,
        int contenderSessionId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        BlockingWaitSnapshot? lastObserved = null;

        while (DateTime.UtcNow < deadline)
        {
            await using var connection = _fixture.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT TOP (1)
                    r.wait_type,
                    r.wait_resource,
                    CAST(r.blocking_session_id AS int),
                    i.name
                FROM sys.dm_exec_requests AS r
                INNER JOIN sys.dm_tran_locks AS l
                    ON l.request_session_id = r.session_id
                   AND l.request_status = N'WAIT'
                   AND l.resource_type = N'KEY'
                INNER JOIN sys.partitions AS p
                    ON p.hobt_id = l.resource_associated_entity_id
                INNER JOIN sys.indexes AS i
                    ON i.object_id = p.object_id
                   AND i.index_id = p.index_id
                WHERE r.session_id = @ContenderSessionId
                  AND r.blocking_session_id = @OwnerSessionId
                  AND r.wait_type LIKE N'LCK_M[_]%'
                  AND r.wait_resource LIKE N'KEY:%'
                  AND i.object_id = OBJECT_ID(N'dbo.OperationalMetricProjectionEvidence')
                ORDER BY
                    CASE WHEN i.name = @ExpectedIndexName THEN 0 ELSE 1 END,
                    i.index_id;
                """;
            command.Parameters.Add("@OwnerSessionId", SqlDbType.Int).Value = ownerSessionId;
            command.Parameters.Add("@ContenderSessionId", SqlDbType.Int).Value = contenderSessionId;
            command.Parameters.Add("@ExpectedIndexName", SqlDbType.NVarChar, 128).Value =
                EvidenceOrdinalIndexName;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                lastObserved = new BlockingWaitSnapshot(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetString(3));

                if (string.Equals(
                    lastObserved.IndexName,
                    EvidenceOrdinalIndexName,
                    StringComparison.Ordinal))
                {
                    return lastObserved;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }

        throw new Xunit.Sdk.XunitException(
            lastObserved is null
                ? $"Contender session {contenderSessionId} did not expose a live evidence KEY lock wait blocked by owner session {ownerSessionId}."
                : $"Contender session {contenderSessionId} exposed an evidence lock wait on index '{lastObserved.IndexName}', not '{EvidenceOrdinalIndexName}'.");
    }

    private static async Task<int> ReadSessionIdAsync(
        SqlServerOperationalMetricProjectionCommitContext context,
        CancellationToken cancellationToken) =>
        await ReadSessionIdAsync(
            context.Connection,
            context.Transaction,
            cancellationToken);

    private static async Task<int> ReadSessionIdAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT @@SPID;";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task CleanupHeldWriterAsync(HeldWriter held)
    {
        held.Release.TrySetResult(true);
        if (!held.WriterTask.IsCompleted)
        {
            try
            {
                await held.WriterTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch
            {
                // Cleanup only; preserve the primary assertion failure.
            }
        }
    }

    private async Task<int> CountProjectionRowsAsync(
        OperationalMetricProjectionProcessorId processorId) =>
        await CountRowsAsync(processorId, manifest: false);

    private async Task<int> CountManifestRowsAsync(
        OperationalMetricProjectionProcessorId processorId) =>
        await CountRowsAsync(processorId, manifest: true);

    private async Task<int> CountEvidenceRowsAsync(
        OperationalMetricProjectionProcessorId processorId)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM dbo.OperationalMetricProjectionEvidence AS e
            INNER JOIN dbo.OperationalMetricProjection AS p
                ON p.OperationalMetricProjectionRowId = e.OperationalMetricProjectionRowId
            INNER JOIN dbo.OperationalMetricProjectionProcessor AS pp
                ON pp.OperationalMetricProjectionProcessorRowId = p.OperationalMetricProjectionProcessorRowId
            WHERE pp.ProcessorKeyBinary = @ProcessorKeyBinary;
            """;
        command.Parameters.Add(
            "@ProcessorKeyBinary",
            SqlDbType.VarBinary,
            StringOrderKeyV2Codec.MaximumEncodedLength).Value =
            StringOrderKeyV2Codec.Encode(processorId.Value);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<int> CountRowsAsync(
        OperationalMetricProjectionProcessorId processorId,
        bool manifest)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = manifest
            ? """
                SELECT COUNT(*)
                FROM dbo.OperationalMetricProjectionManifest AS x
                INNER JOIN dbo.OperationalMetricProjectionProcessor AS pp
                    ON pp.OperationalMetricProjectionProcessorRowId = x.OperationalMetricProjectionProcessorRowId
                WHERE pp.ProcessorKeyBinary = @ProcessorKeyBinary;
                """
            : """
                SELECT COUNT(*)
                FROM dbo.OperationalMetricProjection AS x
                INNER JOIN dbo.OperationalMetricProjectionProcessor AS pp
                    ON pp.OperationalMetricProjectionProcessorRowId = x.OperationalMetricProjectionProcessorRowId
                WHERE pp.ProcessorKeyBinary = @ProcessorKeyBinary;
                """;
        command.Parameters.Add(
            "@ProcessorKeyBinary",
            SqlDbType.VarBinary,
            StringOrderKeyV2Codec.MaximumEncodedLength).Value =
            StringOrderKeyV2Codec.Encode(processorId.Value);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static OperationalMetricProjectionCommit CreateReplayCommit(PublishedFixture published)
    {
        var proposed = new OperationalMetricProjectionCheckpoint(
            published.ProcessorId,
            published.Source.Checkpoint,
            new OperationalMetricProjectionBatchManifest([published.Projection.Key]));
        return new OperationalMetricProjectionCommit(
            published.ProcessorId,
            expectedCheckpoint: null,
            proposed,
            [published.Projection]);
    }

    private static OperationalMetricEvaluationKey CreateShiftKey(
        MachineId machineId,
        string metricKey,
        string version)
    {
        var occurrence = new ShiftOccurrenceId(
            new SiteId("SITE-1"),
            new ShiftScheduleAssignmentId("SCHEDULE-A"),
            new ShiftId("SHIFT-A"),
            new DateTimeOffset(2026, 9, 7, 6, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 7, 14, 0, 0, TimeSpan.Zero));
        return new OperationalMetricEvaluationKey(
            machineId,
            new OperationalMetricPeriodId.Shift(occurrence),
            new OperationalMetricDefinitionId(metricKey, version),
            OperationalMetricEvaluationContextKey.Unpartitioned);
    }

    private async Task<SourceFixture> CreateSourceAsync()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var fact = await inputStore.AppendAsync(
            CreateAppend(
                machineId,
                $"evidence-lock-source-{Guid.NewGuid():N}"),
            CancellationToken.None);
        var processorId = new MetricAggregationProcessorId(
            $"evidence-lock-source-{Guid.NewGuid():N}");
        var checkpoint = new MetricAggregationCheckpoint(
            processorId,
            fact.StreamId,
            fact.Position);
        var aggregationStore = new SqlServerMetricAggregationStore(
            _fixture.ConnectionString);
        await aggregationStore.CommitAsync(
            new MetricAggregationCommit(processorId, null, checkpoint, []),
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
        var start = new DateTimeOffset(2026, 9, 7, 6, 0, 0, TimeSpan.Zero);
        var fact = new DurableMetricInputFact
        {
            Id = new MetricInputFactId(factId),
            Key = "running-duration",
            Value = 1m,
            Unit = "seconds",
            StartsAtUtc = start,
            EndsAtUtc = start.AddMinutes(1),
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
                start,
                start.AddHours(8)),
            new ProductionDayId(
                siteId,
                DateOnly.FromDateTime(start.UtcDateTime)));
    }

    private static void AddString(
        SqlCommand command,
        string name,
        string value) =>
        command.Parameters.Add(name, SqlDbType.NVarChar, 256).Value = value;

    private static void AddOrderKey(
        SqlCommand command,
        string name,
        string value) =>
        command.Parameters.Add(name, SqlDbType.VarBinary, 769).Value =
            StringOrderKeyV2Codec.Encode(value);

    private sealed record SourceFixture(
        MachineId MachineId,
        MetricAggregationCheckpoint Checkpoint);

    private sealed record PublishedFixture(
        OperationalMetricProjectionProcessorId ProcessorId,
        SourceFixture Source,
        OperationalMetricProjection Projection,
        long ProcessorRowId,
        long ProjectionRowId);

    private sealed record HeldWriter(
        TaskCompletionSource<bool> BoundaryComplete,
        TaskCompletionSource<int> OwnerSessionId,
        TaskCompletionSource<bool> Release,
        Task WriterTask);

    private sealed record BlockingWaitSnapshot(
        string WaitType,
        string WaitResource,
        int BlockingSessionId,
        string IndexName);

    private sealed class InspectionCompleteException : Exception;
}
