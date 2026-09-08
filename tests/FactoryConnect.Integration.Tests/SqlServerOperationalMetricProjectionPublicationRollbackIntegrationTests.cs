using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerOperationalMetricProjectionPublicationRollbackIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerOperationalMetricProjectionPublicationRollbackIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData(SqlServerOperationalMetricProjectionPublicationStage.EvidenceDeleted)]
    [InlineData(SqlServerOperationalMetricProjectionPublicationStage.ManifestDeleted)]
    [InlineData(SqlServerOperationalMetricProjectionPublicationStage.ObsoleteProjectionDeleted)]
    [InlineData(SqlServerOperationalMetricProjectionPublicationStage.RetainedProjectionUpdated)]
    [InlineData(SqlServerOperationalMetricProjectionPublicationStage.NewProjectionInserted)]
    [InlineData(SqlServerOperationalMetricProjectionPublicationStage.ManifestInserted)]
    [InlineData(SqlServerOperationalMetricProjectionPublicationStage.EvidenceInserted)]
    [InlineData(SqlServerOperationalMetricProjectionPublicationStage.ExactStateRevalidated)]
    public async Task FailureAfterMutationStageRollsBackCompletePublicationAndCheckpoint(
        SqlServerOperationalMetricProjectionPublicationStage failureStage)
    {
        var source = await CreateSourceAsync();
        var processorId = NewProcessorId();
        var retainedKey = CreateShiftKey(source.MachineId, "availability");
        var obsoleteKey = CreateShiftKey(source.MachineId, "performance");

        await ExecutePublicationAsync(CreateInitialCommit(
            processorId,
            source.Checkpoint,
            [
                CreateComponentProjection(
                    processorId,
                    retainedKey,
                    source.Checkpoint,
                    0.50m,
                    "retained-before"),
                CreateComponentProjection(
                    processorId,
                    obsoleteKey,
                    source.Checkpoint,
                    0.80m,
                    "obsolete-before"),
            ]));

        var before = await ReadDurableStateAsync(processorId);
        var nextRevision = Advance(source.Checkpoint);
        var newKey = CreateShiftKey(source.MachineId, "quality");
        var advance = CreateAdvanceCommit(
            processorId,
            source.Checkpoint,
            [retainedKey, obsoleteKey],
            nextRevision,
            [
                CreateComponentProjection(
                    processorId,
                    retainedKey,
                    nextRevision,
                    0.65m,
                    "retained-after"),
                CreateComponentProjection(
                    processorId,
                    newKey,
                    nextRevision,
                    0.95m,
                    "new-after"),
            ]);

        var transaction = new SqlServerOperationalMetricProjectionCommitTransaction(
            _fixture.ConnectionString);
        var failure = await Assert.ThrowsAsync<InjectedPublicationFailureException>(() =>
            transaction.ExecuteAsync(
                advance,
                async (context, cancellationToken) =>
                {
                    await SqlServerOperationalMetricProjectionPublication.ExecuteAsync(
                        context,
                        advance,
                        (stage, _) => stage == failureStage
                            ? Task.FromException(new InjectedPublicationFailureException(stage))
                            : Task.CompletedTask,
                        cancellationToken);
                },
                CancellationToken.None));

        Assert.Equal(failureStage, failure.Stage);

        var after = await ReadDurableStateAsync(processorId);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task ExactStateRevalidationOccursBeforeCheckpointAdvancement()
    {
        var source = await CreateSourceAsync();
        var processorId = NewProcessorId();
        var key = CreateShiftKey(source.MachineId, "availability");
        await ExecutePublicationAsync(CreateInitialCommit(
            processorId,
            source.Checkpoint,
            [CreateComponentProjection(
                processorId,
                key,
                source.Checkpoint,
                0.50m,
                "before")]));

        var before = await ReadDurableStateAsync(processorId);
        var nextRevision = Advance(source.Checkpoint);
        var replacement = CreateAdvanceCommit(
            processorId,
            source.Checkpoint,
            [key],
            nextRevision,
            [CreateComponentProjection(
                processorId,
                key,
                nextRevision,
                0.75m,
                "after")]);

        var transaction = new SqlServerOperationalMetricProjectionCommitTransaction(
            _fixture.ConnectionString);
        var observedStage = false;

        var failure = await Assert.ThrowsAsync<InjectedPublicationFailureException>(() =>
            transaction.ExecuteAsync(
                replacement,
                async (context, cancellationToken) =>
                {
                    await SqlServerOperationalMetricProjectionPublication.ExecuteAsync(
                        context,
                        replacement,
                        async (stage, stageCancellationToken) =>
                        {
                            if (stage != SqlServerOperationalMetricProjectionPublicationStage.ExactStateRevalidated)
                            {
                                return;
                            }

                            observedStage = true;
                            var checkpointPosition = await ReadCheckpointPositionInTransactionAsync(
                                context,
                                stageCancellationToken);
                            Assert.Equal(source.Checkpoint.Position.Value, checkpointPosition);
                            throw new InjectedPublicationFailureException(stage);
                        },
                        cancellationToken);
                },
                CancellationToken.None));

        Assert.True(observedStage);
        Assert.Equal(
            SqlServerOperationalMetricProjectionPublicationStage.ExactStateRevalidated,
            failure.Stage);
        Assert.Equal(before, await ReadDurableStateAsync(processorId));
    }

    private async Task ExecutePublicationAsync(OperationalMetricProjectionCommit commit)
    {
        var transaction = new SqlServerOperationalMetricProjectionCommitTransaction(
            _fixture.ConnectionString);
        await transaction.ExecuteAsync(
            commit,
            async (context, cancellationToken) =>
            {
                await SqlServerOperationalMetricProjectionPublication.ExecuteAsync(
                    context,
                    commit,
                    cancellationToken);
            },
            CancellationToken.None);
    }

    private async Task<DurablePublicationState> ReadDurableStateAsync(
        OperationalMetricProjectionProcessorId processorId)
    {
        var transaction = new SqlServerOperationalMetricProjectionCommitTransaction(
            _fixture.ConnectionString);
        var header = await transaction.ReadCheckpointHeaderAsync(
            processorId,
            CancellationToken.None);
        Assert.NotNull(header);

        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();

        var projectionsJson = await ReadJsonAsync(
            connection,
            """
            SELECT p.*
            FROM dbo.OperationalMetricProjection AS p
            WHERE p.OperationalMetricProjectionProcessorRowId = @ProcessorRowId
            ORDER BY p.OperationalMetricProjectionRowId
            FOR JSON PATH, INCLUDE_NULL_VALUES;
            """,
            header.ProjectionProcessorRowId);

        var manifestJson = await ReadJsonAsync(
            connection,
            """
            SELECT
                m.OperationalMetricProjectionProcessorRowId,
                m.OperationalMetricProjectionRowId
            FROM dbo.OperationalMetricProjectionManifest AS m
            WHERE m.OperationalMetricProjectionProcessorRowId = @ProcessorRowId
            ORDER BY m.OperationalMetricProjectionRowId
            FOR JSON PATH, INCLUDE_NULL_VALUES;
            """,
            header.ProjectionProcessorRowId);

        var evidenceJson = await ReadJsonAsync(
            connection,
            """
            SELECT e.*
            FROM dbo.OperationalMetricProjectionEvidence AS e
            INNER JOIN dbo.OperationalMetricProjection AS p
                ON p.OperationalMetricProjectionRowId = e.OperationalMetricProjectionRowId
            WHERE p.OperationalMetricProjectionProcessorRowId = @ProcessorRowId
            ORDER BY
                e.OperationalMetricProjectionRowId,
                e.EvidenceKind,
                e.EvidenceOrdinal
            FOR JSON PATH, INCLUDE_NULL_VALUES;
            """,
            header.ProjectionProcessorRowId);

        return new DurablePublicationState(
            header.Position.Value,
            projectionsJson,
            manifestJson,
            evidenceJson);
    }

    private static async Task<string> ReadJsonAsync(
        SqlConnection connection,
        string sql,
        long processorRowId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
        return Convert.ToString(
                   await command.ExecuteScalarAsync(CancellationToken.None),
                   System.Globalization.CultureInfo.InvariantCulture)
               ?? string.Empty;
    }

    private static async Task<ulong> ReadCheckpointPositionInTransactionAsync(
        SqlServerOperationalMetricProjectionCommitContext context,
        CancellationToken cancellationToken)
    {
        await using var command = context.Connection.CreateCommand();
        command.Transaction = context.Transaction;
        command.CommandText = """
            SELECT Position
            FROM dbo.OperationalMetricProjectionCheckpoint
            WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId;
            """;
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value =
            context.ProjectionProcessorRowId;
        return SqlServerUInt64.Materialize(
            Convert.ToDecimal(
                await command.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture));
    }

    private static OperationalMetricProjection CreateComponentProjection(
        OperationalMetricProjectionProcessorId processorId,
        OperationalMetricEvaluationKey key,
        MetricAggregationCheckpoint revision,
        decimal value,
        string operandName)
    {
        var period = Assert.IsType<OperationalMetricPeriodId.Shift>(key.PeriodId);
        var sourceIdentity = new OperationalMetricAggregateSourceIdentity(
            revision.ProcessorId,
            key.MachineId,
            key.PeriodId,
            "running-duration");
        var evidence = new OperationalMetricComponentProjectionEvidence(
            operandName,
            sourceIdentity,
            revision,
            (MetricDimension)0,
            60m,
            "seconds",
            1,
            period.ShiftOccurrenceId.StartsAtUtc,
            period.ShiftOccurrenceId.StartsAtUtc.AddMinutes(1));
        return new OperationalMetricProjection(
            processorId,
            key,
            OperationalMetricEvaluationStatus.Calculated,
            value,
            "ratio",
            reasonCode: null,
            reasonOperandName: null,
            revision,
            [evidence],
            dependencyEvidence: null);
    }

    private static OperationalMetricProjectionCommit CreateInitialCommit(
        OperationalMetricProjectionProcessorId processorId,
        MetricAggregationCheckpoint revision,
        IReadOnlyList<OperationalMetricProjection> projections) =>
        new(
            processorId,
            expectedCheckpoint: null,
            new OperationalMetricProjectionCheckpoint(
                processorId,
                revision,
                new OperationalMetricProjectionBatchManifest(
                    projections.Select(static projection => projection.Key))),
            projections);

    private static OperationalMetricProjectionCommit CreateAdvanceCommit(
        OperationalMetricProjectionProcessorId processorId,
        MetricAggregationCheckpoint currentRevision,
        IReadOnlyList<OperationalMetricEvaluationKey> currentKeys,
        MetricAggregationCheckpoint proposedRevision,
        IReadOnlyList<OperationalMetricProjection> projections) =>
        new(
            processorId,
            new OperationalMetricProjectionCheckpoint(
                processorId,
                currentRevision,
                new OperationalMetricProjectionBatchManifest(currentKeys)),
            new OperationalMetricProjectionCheckpoint(
                processorId,
                proposedRevision,
                new OperationalMetricProjectionBatchManifest(
                    projections.Select(static projection => projection.Key))),
            projections);

    private static MetricAggregationCheckpoint Advance(MetricAggregationCheckpoint checkpoint) =>
        new(
            checkpoint.ProcessorId,
            checkpoint.StreamId,
            new MetricInputPosition(checkpoint.Position.Value + 1));

    private static OperationalMetricEvaluationKey CreateShiftKey(
        MachineId machineId,
        string metricKey)
    {
        var occurrence = new ShiftOccurrenceId(
            new SiteId("SITE-1"),
            new ShiftScheduleAssignmentId("SCHEDULE-A"),
            new ShiftId("SHIFT-A"),
            new DateTimeOffset(2026, 9, 8, 6, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 8, 14, 0, 0, TimeSpan.Zero));
        return new OperationalMetricEvaluationKey(
            machineId,
            new OperationalMetricPeriodId.Shift(occurrence),
            new OperationalMetricDefinitionId(metricKey, "1"),
            OperationalMetricEvaluationContextKey.Unpartitioned);
    }

    private async Task<SourceFixture> CreateSourceAsync()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var fact = await inputStore.AppendAsync(
            CreateAppend(machineId, $"publication-rollback-source-{Guid.NewGuid():N}"),
            CancellationToken.None);
        var processorId = new MetricAggregationProcessorId(
            $"publication-rollback-source-{Guid.NewGuid():N}");
        var checkpoint = new MetricAggregationCheckpoint(
            processorId,
            fact.StreamId,
            fact.Position);
        var aggregationStore = new SqlServerMetricAggregationStore(_fixture.ConnectionString);
        await aggregationStore.CommitAsync(
            new MetricAggregationCommit(processorId, null, checkpoint, []),
            CancellationToken.None);
        return new SourceFixture(machineId, checkpoint);
    }

    private static DurableMetricInputAppend CreateAppend(MachineId machineId, string factId)
    {
        var siteId = new SiteId("SITE-1");
        var shiftId = new ShiftId("SHIFT-A");
        var scheduleId = new ShiftScheduleAssignmentId("SCHEDULE-A");
        var start = new DateTimeOffset(2026, 9, 8, 6, 0, 0, TimeSpan.Zero);
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

    private static OperationalMetricProjectionProcessorId NewProcessorId() =>
        new($"publication-rollback-{Guid.NewGuid():N}");

    private sealed record SourceFixture(
        MachineId MachineId,
        MetricAggregationCheckpoint Checkpoint);

    private sealed record DurablePublicationState(
        ulong CheckpointPosition,
        string ProjectionsJson,
        string ManifestJson,
        string EvidenceJson);

    private sealed class InjectedPublicationFailureException : Exception
    {
        public InjectedPublicationFailureException(
            SqlServerOperationalMetricProjectionPublicationStage stage)
            : base($"Injected publication failure after stage '{stage}'.")
        {
            Stage = stage;
        }

        public SqlServerOperationalMetricProjectionPublicationStage Stage { get; }
    }
}
