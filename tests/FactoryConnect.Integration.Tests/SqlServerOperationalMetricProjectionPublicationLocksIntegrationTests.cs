using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerOperationalMetricProjectionPublicationLocksIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerOperationalMetricProjectionPublicationLocksIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EmptyCurrentPublicationAcquiresEmptyManifestAndEvidenceBoundary()
    {
        var source = await CreateSourceAsync();
        var processorId = NewProcessorId();
        var projection = CreateCalculated(
            processorId,
            CreateShiftKey(source.MachineId, "availability", "1"),
            source.Checkpoint,
            0.5m);
        var commit = CreateInitialCommit(processorId, source.Checkpoint, [projection]);
        var sut = new SqlServerOperationalMetricProjectionCommitTransaction(_fixture.ConnectionString);

        await Assert.ThrowsAsync<InspectionCompleteException>(() =>
            sut.ExecuteAsync(
                commit,
                async (context, cancellationToken) =>
                {
                    var prepared = await SqlServerOperationalMetricProjectionPublicationLocks.PrepareAsync(
                        context,
                        commit,
                        cancellationToken);

                    Assert.Single(prepared.ProjectionPlan.ProposedRows);
                    Assert.Empty(prepared.ProjectionPlan.ObsoleteProjectionRowIds);
                    Assert.Empty(prepared.Locks.ManifestProjectionRowIds);
                    Assert.Empty(prepared.Locks.EvidenceRows);
                    await AssertZeroPublicationMutationAsync(context, cancellationToken);
                    throw new InspectionCompleteException();
                },
                CancellationToken.None));

        await AssertNoPublicationAsync(processorId);
    }

    [Fact]
    public async Task RetainedPublicationLocksManifestAndEvidenceAfterProjectionPreparation()
    {
        var published = await CreatePublishedAsync(1, includeEvidence: true);
        var projection = CreateCalculated(
            published.ProcessorId,
            published.Keys[0],
            published.Source.Checkpoint,
            0.5m);
        var commit = CreateReplayCommit(published, [projection]);
        var sut = new SqlServerOperationalMetricProjectionCommitTransaction(_fixture.ConnectionString);

        await Assert.ThrowsAsync<InspectionCompleteException>(() =>
            sut.ExecuteAsync(
                commit,
                async (context, cancellationToken) =>
                {
                    var prepared = await SqlServerOperationalMetricProjectionPublicationLocks.PrepareAsync(
                        context,
                        commit,
                        cancellationToken);

                    var proposed = Assert.Single(prepared.ProjectionPlan.ProposedRows);
                    Assert.Equal(published.RowIds[0], proposed.ExistingProjectionRowId);
                    Assert.Equal(published.RowIds, prepared.Locks.ManifestProjectionRowIds);
                    var evidence = Assert.Single(prepared.Locks.EvidenceRows);
                    Assert.Equal(published.RowIds[0], evidence.OperationalMetricProjectionRowId);
                    Assert.Equal((byte)1, evidence.EvidenceKind);
                    Assert.Equal(0, evidence.EvidenceOrdinal);
                    await AssertZeroPublicationMutationAsync(context, cancellationToken, expectedRows: 1, expectedEvidence: 1);
                    throw new InspectionCompleteException();
                },
                CancellationToken.None));
    }

    [Fact]
    public async Task ShrinkingPublicationValidatesManifestAgainstRetainedPlusObsoleteRows()
    {
        var published = await CreatePublishedAsync(2, includeEvidence: false);
        var nextRevision = Advance(published.Source.Checkpoint);
        var retained = CreateCalculated(
            published.ProcessorId,
            published.Keys[0],
            nextRevision,
            0.6m);
        var commit = CreateAdvanceCommit(published, nextRevision, [retained]);
        var sut = new SqlServerOperationalMetricProjectionCommitTransaction(_fixture.ConnectionString);

        await Assert.ThrowsAsync<InspectionCompleteException>(() =>
            sut.ExecuteAsync(
                commit,
                async (context, cancellationToken) =>
                {
                    var prepared = await SqlServerOperationalMetricProjectionPublicationLocks.PrepareAsync(
                        context,
                        commit,
                        cancellationToken);

                    Assert.Equal(published.RowIds[0], Assert.Single(prepared.ProjectionPlan.ProposedRows).ExistingProjectionRowId);
                    Assert.Equal([published.RowIds[1]], prepared.ProjectionPlan.ObsoleteProjectionRowIds);
                    Assert.Equal(published.RowIds, prepared.Locks.ManifestProjectionRowIds);
                    await AssertZeroPublicationMutationAsync(context, cancellationToken, expectedRows: 2);
                    throw new InspectionCompleteException();
                },
                CancellationToken.None));
    }

    [Fact]
    public async Task EmptyReplacementValidatesEntireOldManifestAsObsolete()
    {
        var published = await CreatePublishedAsync(2, includeEvidence: false);
        var nextRevision = Advance(published.Source.Checkpoint);
        var commit = CreateAdvanceCommit(published, nextRevision, []);
        var sut = new SqlServerOperationalMetricProjectionCommitTransaction(_fixture.ConnectionString);

        await Assert.ThrowsAsync<InspectionCompleteException>(() =>
            sut.ExecuteAsync(
                commit,
                async (context, cancellationToken) =>
                {
                    var prepared = await SqlServerOperationalMetricProjectionPublicationLocks.PrepareAsync(
                        context,
                        commit,
                        cancellationToken);

                    Assert.Empty(prepared.ProjectionPlan.ProposedRows);
                    Assert.Equal(published.RowIds, prepared.ProjectionPlan.ObsoleteProjectionRowIds);
                    Assert.Equal(published.RowIds, prepared.Locks.ManifestProjectionRowIds);
                    await AssertZeroPublicationMutationAsync(context, cancellationToken, expectedRows: 2);
                    throw new InspectionCompleteException();
                },
                CancellationToken.None));
    }

    [Fact]
    public async Task ManifestMismatchIsRejectedBeforeEvidenceAcquisitionCanAuthorizePublication()
    {
        var published = await CreatePublishedAsync(1, includeEvidence: true);
        await DeleteManifestRowAsync(published.ProcessorRowId, published.RowIds[0]);
        var projection = CreateCalculated(
            published.ProcessorId,
            published.Keys[0],
            published.Source.Checkpoint,
            0.5m);
        var commit = CreateReplayCommit(published, [projection]);
        var sut = new SqlServerOperationalMetricProjectionCommitTransaction(_fixture.ConnectionString);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.ExecuteAsync(
                commit,
                (context, cancellationToken) =>
                    SqlServerOperationalMetricProjectionPublicationLocks.PrepareAsync(
                        context,
                        commit,
                        cancellationToken),
                CancellationToken.None));

        Assert.Equal(
            "Operational metric projection manifest does not exactly match the current durable projection set.",
            exception.Message);
        Assert.Equal(1, await CountProjectionRowsAsync(published.ProcessorId));
        Assert.Equal(1, await CountEvidenceRowsAsync(published.ProcessorId));
    }

    [Fact]
    public async Task ManifestIsAcquiredBeforeEvidence()
    {
        var published = await CreatePublishedAsync(1, includeEvidence: true);
        var projection = CreateCalculated(
            published.ProcessorId,
            published.Keys[0],
            published.Source.Checkpoint,
            0.5m);
        var commit = CreateReplayCommit(published, [projection]);

        await using var blockerConnection = _fixture.CreateConnection();
        await blockerConnection.OpenAsync();
        await using var blockerTransaction = (SqlTransaction)await blockerConnection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            CancellationToken.None);
        await LockManifestRowAsync(
            blockerConnection,
            blockerTransaction,
            published.ProcessorRowId,
            published.RowIds[0]);

        var entered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sut = new SqlServerOperationalMetricProjectionCommitTransaction(_fixture.ConnectionString);
        var writer = sut.ExecuteAsync(
            commit,
            async (context, cancellationToken) =>
            {
                entered.TrySetResult(await ReadSessionIdAsync(context, cancellationToken));
                await SqlServerOperationalMetricProjectionPublicationLocks.PrepareAsync(
                    context,
                    commit,
                    cancellationToken);
                throw new InspectionCompleteException();
            },
            CancellationToken.None);

        var writerSessionId = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var wait = await WaitForLockWaitAsync(writerSessionId, TimeSpan.FromSeconds(10));
        Assert.Contains("OperationalMetricProjectionManifest", wait.ResourceDescription, StringComparison.Ordinal);
        Assert.False(await HasGrantedEvidenceKeyLockAsync(writerSessionId));

        await blockerTransaction.RollbackAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InspectionCompleteException>(() => writer);
    }

    [Fact]
    public async Task RollbackAfterCompleteLockBoundaryLeavesPublicationAndCheckpointUnchanged()
    {
        var published = await CreatePublishedAsync(1, includeEvidence: true);
        var nextRevision = Advance(published.Source.Checkpoint);
        var replacement = CreateCalculated(
            published.ProcessorId,
            published.Keys[0],
            nextRevision,
            0.75m);
        var commit = CreateAdvanceCommit(published, nextRevision, [replacement]);
        var sut = new SqlServerOperationalMetricProjectionCommitTransaction(_fixture.ConnectionString);

        await Assert.ThrowsAsync<InspectionCompleteException>(() =>
            sut.ExecuteAsync(
                commit,
                async (context, cancellationToken) =>
                {
                    await SqlServerOperationalMetricProjectionPublicationLocks.PrepareAsync(
                        context,
                        commit,
                        cancellationToken);
                    await AssertZeroPublicationMutationAsync(context, cancellationToken, expectedRows: 1, expectedEvidence: 1);
                    throw new InspectionCompleteException();
                },
                CancellationToken.None));

        var header = await sut.ReadCheckpointHeaderAsync(published.ProcessorId, CancellationToken.None);
        Assert.NotNull(header);
        Assert.Equal(published.Source.Checkpoint.Position, header.Position);
        Assert.Equal(1, await CountProjectionRowsAsync(published.ProcessorId));
        Assert.Equal(1, await CountManifestRowsAsync(published.ProcessorId));
        Assert.Equal(1, await CountEvidenceRowsAsync(published.ProcessorId));
    }

    private async Task<PublishedFixture> CreatePublishedAsync(int projectionCount, bool includeEvidence)
    {
        var source = await CreateSourceAsync();
        var processorId = NewProcessorId();
        var initial = CreateInitialCommit(processorId, source.Checkpoint, []);
        var transaction = new SqlServerOperationalMetricProjectionCommitTransaction(_fixture.ConnectionString);
        await transaction.ExecuteAsync(initial, static (_, _) => Task.CompletedTask, CancellationToken.None);
        var header = await transaction.ReadCheckpointHeaderAsync(processorId, CancellationToken.None);
        Assert.NotNull(header);

        var keys = Enumerable.Range(0, projectionCount)
            .Select(index => CreateShiftKey(source.MachineId, $"metric-{index:D2}", "1"))
            .ToArray();
        var rowIds = new List<long>();
        foreach (var key in keys)
        {
            rowIds.Add(await SeedProjectionAsync(header.ProjectionProcessorRowId, source.Checkpoint, key, includeEvidence));
        }

        rowIds.Sort();
        return new PublishedFixture(processorId, source, header.ProjectionProcessorRowId, keys, rowIds.ToArray());
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
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO dbo.OperationalMetricProjection
            (
                OperationalMetricProjectionProcessorRowId, EvaluationKeyCodecVersion, EvaluationKeyHash, EvaluationKeyBinary,
                MachineId, PeriodKind, PeriodSiteId, PeriodSiteOrderKey,
                ShiftScheduleAssignmentId, ShiftScheduleAssignmentOrderKey, ShiftId, ShiftOrderKey,
                ShiftStartsAtUtc, ShiftEndsAtUtc, ProductionBusinessDate,
                ProductionOrderPresent, ProductionOrderId, ProductionOrderOrderKey,
                OperationPresent, OperationId, OperationOrderKey, PartPresent, PartId, PartOrderKey,
                OperatorPresent, OperatorId, OperatorOrderKey,
                MetricKey, MetricKeyOrderKey, DefinitionVersion, DefinitionVersionOrderKey,
                Status, MetricValue, Unit, ReasonCode, ReasonOperandName, SourceRevisionPosition
            )
            OUTPUT INSERTED.OperationalMetricProjectionRowId
            VALUES
            (
                @ProcessorRowId, 1, @Hash, @Binary,
                @MachineId, 1, @SiteId, @SiteOrderKey,
                @ScheduleId, @ScheduleOrderKey, @ShiftId, @ShiftOrderKey,
                @StartsAtUtc, @EndsAtUtc, NULL,
                0, NULL, NULL, 0, NULL, NULL, 0, NULL, NULL, 0, NULL, NULL,
                @MetricKey, @MetricOrderKey, @Version, @VersionOrderKey,
                0, N'0.5', N'ratio', NULL, NULL, @Position
            );
            """;
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
        command.Parameters.Add("@Hash", SqlDbType.Binary, 32).Value = hash;
        command.Parameters.Add("@Binary", SqlDbType.VarBinary, -1).Value = binary;
        command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value = key.MachineId.Value;
        AddString(command, "@SiteId", shift.ShiftOccurrenceId.SiteId.Value);
        AddOrderKey(command, "@SiteOrderKey", shift.ShiftOccurrenceId.SiteId.Value);
        AddString(command, "@ScheduleId", shift.ShiftOccurrenceId.ShiftScheduleAssignmentId.Value);
        AddOrderKey(command, "@ScheduleOrderKey", shift.ShiftOccurrenceId.ShiftScheduleAssignmentId.Value);
        AddString(command, "@ShiftId", shift.ShiftOccurrenceId.ShiftId.Value);
        AddOrderKey(command, "@ShiftOrderKey", shift.ShiftOccurrenceId.ShiftId.Value);
        command.Parameters.Add("@StartsAtUtc", SqlDbType.DateTimeOffset).Value = shift.ShiftOccurrenceId.StartsAtUtc;
        command.Parameters.Add("@EndsAtUtc", SqlDbType.DateTimeOffset).Value = shift.ShiftOccurrenceId.EndsAtUtc;
        AddString(command, "@MetricKey", key.DefinitionId.MetricKey);
        AddOrderKey(command, "@MetricOrderKey", key.DefinitionId.MetricKey);
        AddString(command, "@Version", key.DefinitionId.Version);
        AddOrderKey(command, "@VersionOrderKey", key.DefinitionId.Version);
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@Position", sourceRevision.Position.Value));
        var rowId = Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);

        await using var manifest = connection.CreateCommand();
        manifest.Transaction = transaction;
        manifest.CommandText = "INSERT INTO dbo.OperationalMetricProjectionManifest (OperationalMetricProjectionProcessorRowId, OperationalMetricProjectionRowId) VALUES (@ProcessorRowId, @ProjectionRowId);";
        manifest.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
        manifest.Parameters.Add("@ProjectionRowId", SqlDbType.BigInt).Value = rowId;
        await manifest.ExecuteNonQueryAsync();

        if (includeEvidence)
        {
            await using var evidence = connection.CreateCommand();
            evidence.Transaction = transaction;
            evidence.CommandText = """
                INSERT INTO dbo.OperationalMetricProjectionEvidence
                (
                    OperationalMetricProjectionRowId, EvidenceKind, EvidenceOrdinal,
                    OperandName, OperandNameOrderKey, ComponentKey, MetricDimension,
                    ComponentValue, ComponentUnit, InputCount, FirstInputTimestamp, LastInputTimestamp
                )
                VALUES
                (
                    @ProjectionRowId, 1, 0,
                    N'component', @OperandOrderKey, N'running-duration', 0,
                    N'1', N'seconds', 1, @First, @Last
                );
                """;
            evidence.Parameters.Add("@ProjectionRowId", SqlDbType.BigInt).Value = rowId;
            evidence.Parameters.Add("@OperandOrderKey", SqlDbType.VarBinary, 769).Value = StringOrderKeyV2Codec.Encode("component");
            evidence.Parameters.Add("@First", SqlDbType.DateTimeOffset).Value = shift.ShiftOccurrenceId.StartsAtUtc;
            evidence.Parameters.Add("@Last", SqlDbType.DateTimeOffset).Value = shift.ShiftOccurrenceId.StartsAtUtc.AddMinutes(1);
            await evidence.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return rowId;
    }

    private async Task DeleteManifestRowAsync(long processorRowId, long projectionRowId)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM dbo.OperationalMetricProjectionManifest WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId AND OperationalMetricProjectionRowId = @ProjectionRowId;";
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
        command.Parameters.Add("@ProjectionRowId", SqlDbType.BigInt).Value = projectionRowId;
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task LockManifestRowAsync(SqlConnection connection, SqlTransaction transaction, long processorRowId, long projectionRowId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT OperationalMetricProjectionRowId FROM dbo.OperationalMetricProjectionManifest WITH (UPDLOCK, HOLDLOCK, INDEX(PK_OperationalMetricProjectionManifest)) WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId AND OperationalMetricProjectionRowId = @ProjectionRowId;";
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
        command.Parameters.Add("@ProjectionRowId", SqlDbType.BigInt).Value = projectionRowId;
        Assert.Equal(projectionRowId, Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
    }

    private async Task<LockWaitSnapshot> WaitForLockWaitAsync(int sessionId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await using var connection = _fixture.CreateConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT TOP (1),
                    r.wait_resource,
                    COALESCE(OBJECT_NAME(p.object_id), N'')
                FROM sys.dm_exec_requests AS r
                LEFT JOIN sys.dm_tran_locks AS l
                    ON l.request_session_id = r.session_id AND l.request_status = N'WAIT'
                LEFT JOIN sys.partitions AS p
                    ON p.hobt_id = l.resource_associated_entity_id
                WHERE r.session_id = @SessionId AND r.wait_type LIKE N'LCK_M[_]%';
                """;
            command.Parameters.Add("@SessionId", SqlDbType.Int).Value = sessionId;
            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new LockWaitSnapshot(reader.IsDBNull(0) ? string.Empty : reader.GetString(0), reader.IsDBNull(1) ? string.Empty : reader.GetString(1));
            }
            await Task.Delay(50);
        }
        throw new Xunit.Sdk.XunitException($"Session {sessionId} did not expose a live SQL lock wait.");
    }

    private async Task<bool> HasGrantedEvidenceKeyLockAsync(int sessionId)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM sys.dm_tran_locks AS l
            INNER JOIN sys.partitions AS p ON p.hobt_id = l.resource_associated_entity_id
            WHERE l.request_session_id = @SessionId
              AND l.request_status = N'GRANT'
              AND l.resource_type = N'KEY'
              AND p.object_id = OBJECT_ID(N'dbo.OperationalMetricProjectionEvidence');
            """;
        command.Parameters.Add("@SessionId", SqlDbType.Int).Value = sessionId;
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static async Task<int> ReadSessionIdAsync(SqlServerOperationalMetricProjectionCommitContext context, CancellationToken cancellationToken)
    {
        await using var command = context.Connection.CreateCommand();
        command.Transaction = context.Transaction;
        command.CommandText = "SELECT @@SPID;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task AssertNoPublicationAsync(OperationalMetricProjectionProcessorId processorId)
    {
        Assert.Equal(0, await CountProjectionRowsAsync(processorId));
        Assert.Equal(0, await CountManifestRowsAsync(processorId));
        Assert.Equal(0, await CountEvidenceRowsAsync(processorId));
    }

    private static async Task AssertZeroPublicationMutationAsync(
        SqlServerOperationalMetricProjectionCommitContext context,
        CancellationToken cancellationToken,
        int expectedRows = 0,
        int expectedEvidence = 0)
    {
        await using var command = context.Connection.CreateCommand();
        command.Transaction = context.Transaction;
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM dbo.OperationalMetricProjection WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId),
                (SELECT COUNT(*) FROM dbo.OperationalMetricProjectionManifest WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId),
                (SELECT COUNT(*) FROM dbo.OperationalMetricProjectionEvidence AS e INNER JOIN dbo.OperationalMetricProjection AS p ON p.OperationalMetricProjectionRowId = e.OperationalMetricProjectionRowId WHERE p.OperationalMetricProjectionProcessorRowId = @ProcessorRowId);
            """;
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = context.ProjectionProcessorRowId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        Assert.Equal(expectedRows, reader.GetInt32(0));
        Assert.Equal(expectedRows, reader.GetInt32(1));
        Assert.Equal(expectedEvidence, reader.GetInt32(2));
    }

    private async Task<int> CountProjectionRowsAsync(OperationalMetricProjectionProcessorId processorId) => await CountRowsAsync(processorId, "OperationalMetricProjection");
    private async Task<int> CountManifestRowsAsync(OperationalMetricProjectionProcessorId processorId) => await CountRowsAsync(processorId, "OperationalMetricProjectionManifest");

    private async Task<int> CountEvidenceRowsAsync(OperationalMetricProjectionProcessorId processorId)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM dbo.OperationalMetricProjectionEvidence AS e
            INNER JOIN dbo.OperationalMetricProjection AS p ON p.OperationalMetricProjectionRowId = e.OperationalMetricProjectionRowId
            INNER JOIN dbo.OperationalMetricProjectionProcessor AS pp ON pp.OperationalMetricProjectionProcessorRowId = p.OperationalMetricProjectionProcessorRowId
            WHERE pp.ProcessorKeyBinary = @ProcessorKeyBinary;
            """;
        command.Parameters.Add("@ProcessorKeyBinary", SqlDbType.VarBinary, StringOrderKeyV2Codec.MaximumEncodedLength).Value = StringOrderKeyV2Codec.Encode(processorId.Value);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<int> CountRowsAsync(OperationalMetricProjectionProcessorId processorId, string tableName)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = tableName == "OperationalMetricProjection"
            ? "SELECT COUNT(*) FROM dbo.OperationalMetricProjection AS x INNER JOIN dbo.OperationalMetricProjectionProcessor AS pp ON pp.OperationalMetricProjectionProcessorRowId = x.OperationalMetricProjectionProcessorRowId WHERE pp.ProcessorKeyBinary = @ProcessorKeyBinary;"
            : "SELECT COUNT(*) FROM dbo.OperationalMetricProjectionManifest AS x INNER JOIN dbo.OperationalMetricProjectionProcessor AS pp ON pp.OperationalMetricProjectionProcessorRowId = x.OperationalMetricProjectionProcessorRowId WHERE pp.ProcessorKeyBinary = @ProcessorKeyBinary;";
        command.Parameters.Add("@ProcessorKeyBinary", SqlDbType.VarBinary, StringOrderKeyV2Codec.MaximumEncodedLength).Value = StringOrderKeyV2Codec.Encode(processorId.Value);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static OperationalMetricProjectionProcessorId NewProcessorId() => new($"publication-{Guid.NewGuid():N}");

    private static OperationalMetricProjectionCommit CreateInitialCommit(OperationalMetricProjectionProcessorId processorId, MetricAggregationCheckpoint revision, IReadOnlyList<OperationalMetricProjection> projections)
    {
        var checkpoint = new OperationalMetricProjectionCheckpoint(processorId, revision, new OperationalMetricProjectionBatchManifest(projections.Select(static p => p.Key)));
        return new OperationalMetricProjectionCommit(processorId, null, checkpoint, projections);
    }

    private static OperationalMetricProjectionCommit CreateReplayCommit(PublishedFixture published, IReadOnlyList<OperationalMetricProjection> projections)
    {
        var checkpoint = new OperationalMetricProjectionCheckpoint(published.ProcessorId, published.Source.Checkpoint, new OperationalMetricProjectionBatchManifest(projections.Select(static p => p.Key)));
        return new OperationalMetricProjectionCommit(published.ProcessorId, null, checkpoint, projections);
    }

    private static OperationalMetricProjectionCommit CreateAdvanceCommit(PublishedFixture published, MetricAggregationCheckpoint revision, IReadOnlyList<OperationalMetricProjection> projections)
    {
        var expected = new OperationalMetricProjectionCheckpoint(published.ProcessorId, published.Source.Checkpoint, new OperationalMetricProjectionBatchManifest(published.Keys));
        var proposed = new OperationalMetricProjectionCheckpoint(published.ProcessorId, revision, new OperationalMetricProjectionBatchManifest(projections.Select(static p => p.Key)));
        return new OperationalMetricProjectionCommit(published.ProcessorId, expected, proposed, projections);
    }

    private static MetricAggregationCheckpoint Advance(MetricAggregationCheckpoint checkpoint) =>
        new(checkpoint.ProcessorId, checkpoint.StreamId, new MetricInputPosition(checkpoint.Position.Value + 1));

    private static OperationalMetricProjection CreateCalculated(OperationalMetricProjectionProcessorId processorId, OperationalMetricEvaluationKey key, MetricAggregationCheckpoint revision, decimal value) =>
        new(processorId, key, OperationalMetricEvaluationStatus.Calculated, value, "ratio", null, null, revision);

    private static OperationalMetricEvaluationKey CreateShiftKey(MachineId machineId, string metricKey, string version)
    {
        var occurrence = new ShiftOccurrenceId(new SiteId("SITE-1"), new ShiftScheduleAssignmentId("SCHEDULE-A"), new ShiftId("SHIFT-A"), new DateTimeOffset(2026, 9, 7, 6, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 7, 14, 0, 0, TimeSpan.Zero));
        return new OperationalMetricEvaluationKey(machineId, new OperationalMetricPeriodId.Shift(occurrence), new OperationalMetricDefinitionId(metricKey, version), OperationalMetricEvaluationContextKey.Unpartitioned);
    }

    private async Task<SourceFixture> CreateSourceAsync()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var fact = await inputStore.AppendAsync(CreateAppend(machineId, $"publication-source-{Guid.NewGuid():N}"), CancellationToken.None);
        var processorId = new MetricAggregationProcessorId($"publication-source-{Guid.NewGuid():N}");
        var checkpoint = new MetricAggregationCheckpoint(processorId, fact.StreamId, fact.Position);
        var aggregationStore = new SqlServerMetricAggregationStore(_fixture.ConnectionString);
        await aggregationStore.CommitAsync(new MetricAggregationCommit(processorId, null, checkpoint, []), CancellationToken.None);
        return new SourceFixture(machineId, checkpoint);
    }

    private static DurableMetricInputAppend CreateAppend(MachineId machineId, string factId)
    {
        var siteId = new SiteId("SITE-1");
        var shiftId = new ShiftId("SHIFT-A");
        var scheduleId = new ShiftScheduleAssignmentId("SCHEDULE-A");
        var start = new DateTimeOffset(2026, 9, 7, 6, 0, 0, TimeSpan.Zero);
        var fact = new DurableMetricInputFact
        {
            Id = new MetricInputFactId(factId), Key = "running-duration", Value = 1m, Unit = "seconds",
            StartsAtUtc = start, EndsAtUtc = start.AddMinutes(1), CompanyId = new CompanyId("COMP-1"), SiteId = siteId,
            ProductionLineId = new ProductionLineId("LINE-1"), MachineId = machineId, ShiftId = shiftId, ShiftScheduleAssignmentId = scheduleId,
        };
        return new DurableMetricInputAppend(MetricInputStreamId.ForMachine(machineId), fact,
            new ShiftOccurrenceId(siteId, scheduleId, shiftId, start, start.AddHours(8)),
            new ProductionDayId(siteId, DateOnly.FromDateTime(start.UtcDateTime)));
    }

    private static void AddString(SqlCommand command, string name, string value) => command.Parameters.Add(name, SqlDbType.NVarChar, 256).Value = value;
    private static void AddOrderKey(SqlCommand command, string name, string value) => command.Parameters.Add(name, SqlDbType.VarBinary, 769).Value = StringOrderKeyV2Codec.Encode(value);

    private sealed record SourceFixture(MachineId MachineId, MetricAggregationCheckpoint Checkpoint);
    private sealed record PublishedFixture(OperationalMetricProjectionProcessorId ProcessorId, SourceFixture Source, long ProcessorRowId, IReadOnlyList<OperationalMetricEvaluationKey> Keys, IReadOnlyList<long> RowIds);
    private sealed record LockWaitSnapshot(string WaitResource, string ResourceDescription);
    private sealed class InspectionCompleteException : Exception;
}
