using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerOperationalMetricProjectionJointPublicationConformanceIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerOperationalMetricProjectionJointPublicationConformanceIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task DivergentSameProcessorSuccessorsSerializeAndOnlyFirstPublishes()
    {
        var source = await CreateSourceAsync();
        var processorId = NewProcessorId("same-processor");
        var key = CreateShiftKey(source.MachineId, "availability");
        await ExecutePublicationAsync(CreateInitialCommit(
            processorId,
            source.Checkpoint,
            [CreateComponentProjection(processorId, key, source.Checkpoint, 0.50m, "initial")]));

        var nextRevision = Advance(source.Checkpoint);
        var firstCommit = CreateAdvanceCommit(
            processorId,
            source.Checkpoint,
            [key],
            nextRevision,
            [CreateComponentProjection(processorId, key, nextRevision, 0.70m, "winner")]);
        var secondCommit = CreateAdvanceCommit(
            processorId,
            source.Checkpoint,
            [key],
            nextRevision,
            [CreateComponentProjection(processorId, key, nextRevision, 0.90m, "loser")]);

        var firstReachedMutation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEnteredBody = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var firstTransaction = new SqlServerOperationalMetricProjectionCommitTransaction(
            _fixture.ConnectionString);
        var firstTask = firstTransaction.ExecuteAsync(
            firstCommit,
            async (context, cancellationToken) =>
            {
                await SqlServerOperationalMetricProjectionPublication.ExecuteAsync(
                    context,
                    firstCommit,
                    async (stage, stageCancellationToken) =>
                    {
                        if (stage == SqlServerOperationalMetricProjectionPublicationStage.EvidenceDeleted)
                        {
                            firstReachedMutation.TrySetResult(true);
                            await releaseFirst.Task.WaitAsync(stageCancellationToken);
                        }
                    },
                    cancellationToken);
            },
            CancellationToken.None);

        await firstReachedMutation.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var secondTransaction = new SqlServerOperationalMetricProjectionCommitTransaction(
            _fixture.ConnectionString);
        var secondTask = secondTransaction.ExecuteAsync(
            secondCommit,
            async (context, cancellationToken) =>
            {
                secondEnteredBody.TrySetResult(true);
                await SqlServerOperationalMetricProjectionPublication.ExecuteAsync(
                    context,
                    secondCommit,
                    cancellationToken);
            },
            CancellationToken.None);

        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                secondEnteredBody.Task.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.False(secondTask.IsCompleted);
        }
        finally
        {
            releaseFirst.TrySetResult(true);
        }

        await firstTask;
        await Assert.ThrowsAsync<InvalidOperationException>(() => secondTask);
        Assert.False(secondEnteredBody.Task.IsCompleted);

        var finalState = await ReadDurableStateAsync(processorId);
        Assert.Equal(nextRevision.Position.Value, finalState.CheckpointPosition);
        Assert.Contains("0.7", finalState.ProjectionsJson, StringComparison.Ordinal);
        Assert.Contains("winner", finalState.EvidenceJson, StringComparison.Ordinal);
        Assert.DoesNotContain("0.9", finalState.ProjectionsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("loser", finalState.EvidenceJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DistinctProcessorPublicationsConvergeToIndependentState()
    {
        var sourceA = await CreateSourceAsync();
        var sourceB = await CreateSourceAsync();
        var processorA = NewProcessorId("processor-a");
        var processorB = NewProcessorId("processor-b");
        var keyA = CreateShiftKey(sourceA.MachineId, "availability");
        var keyB = CreateShiftKey(sourceB.MachineId, "performance");

        var commitA = CreateInitialCommit(
            processorA,
            sourceA.Checkpoint,
            [CreateComponentProjection(
                processorA,
                keyA,
                sourceA.Checkpoint,
                0.61m,
                "processor-a-evidence")]);
        var commitB = CreateInitialCommit(
            processorB,
            sourceB.Checkpoint,
            [CreateComponentProjection(
                processorB,
                keyB,
                sourceB.Checkpoint,
                0.82m,
                "processor-b-evidence")]);

        await Task.WhenAll(
            ExecutePublicationAsync(commitA),
            ExecutePublicationAsync(commitB));

        var stateA = await ReadDurableStateAsync(processorA);
        var stateB = await ReadDurableStateAsync(processorB);

        Assert.Equal(sourceA.Checkpoint.Position.Value, stateA.CheckpointPosition);
        Assert.Equal(sourceB.Checkpoint.Position.Value, stateB.CheckpointPosition);
        Assert.Contains("availability", stateA.ProjectionsJson, StringComparison.Ordinal);
        Assert.Contains("processor-a-evidence", stateA.EvidenceJson, StringComparison.Ordinal);
        Assert.DoesNotContain("processor-b-evidence", stateA.EvidenceJson, StringComparison.Ordinal);
        Assert.Contains("performance", stateB.ProjectionsJson, StringComparison.Ordinal);
        Assert.Contains("processor-b-evidence", stateB.EvidenceJson, StringComparison.Ordinal);
        Assert.DoesNotContain("processor-a-evidence", stateB.EvidenceJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedAdvanceRetryThenEqualRevisionReplayConvergesExactly()
    {
        var source = await CreateSourceAsync();
        var processorId = NewProcessorId("retry-replay");
        var key = CreateShiftKey(source.MachineId, "availability");
        await ExecutePublicationAsync(CreateInitialCommit(
            processorId,
            source.Checkpoint,
            [CreateComponentProjection(processorId, key, source.Checkpoint, 0.50m, "before")]));
        var before = await ReadDurableStateAsync(processorId);

        var nextRevision = Advance(source.Checkpoint);
        var proposedProjection = CreateComponentProjection(
            processorId,
            key,
            nextRevision,
            0.75m,
            "after");
        var advance = CreateAdvanceCommit(
            processorId,
            source.Checkpoint,
            [key],
            nextRevision,
            [proposedProjection]);

        var transaction = new SqlServerOperationalMetricProjectionCommitTransaction(
            _fixture.ConnectionString);
        await Assert.ThrowsAsync<InjectedPublicationFailureException>(() =>
            transaction.ExecuteAsync(
                advance,
                async (context, cancellationToken) =>
                {
                    await SqlServerOperationalMetricProjectionPublication.ExecuteAsync(
                        context,
                        advance,
                        (stage, _) => stage == SqlServerOperationalMetricProjectionPublicationStage.EvidenceInserted
                            ? Task.FromException(new InjectedPublicationFailureException())
                            : Task.CompletedTask,
                        cancellationToken);
                },
                CancellationToken.None));

        Assert.Equal(before, await ReadDurableStateAsync(processorId));

        await ExecutePublicationAsync(advance);
        var afterRetry = await ReadDurableStateAsync(processorId);
        Assert.Equal(nextRevision.Position.Value, afterRetry.CheckpointPosition);
        Assert.Contains("0.75", afterRetry.ProjectionsJson, StringComparison.Ordinal);
        Assert.Contains("after", afterRetry.EvidenceJson, StringComparison.Ordinal);

        await ExecutePublicationAsync(CreateReplayCommit(
            processorId,
            nextRevision,
            [proposedProjection]));

        Assert.Equal(afterRetry, await ReadDurableStateAsync(processorId));
    }

    [Fact]
    public async Task CheckpointStableReaderRejectsMixedAttemptAndAcceptsOnlyCompleteNewPublication()
    {
        var source = await CreateSourceAsync();
        var processorId = NewProcessorId("reader");
        var key = CreateShiftKey(source.MachineId, "availability");
        await ExecutePublicationAsync(CreateInitialCommit(
            processorId,
            source.Checkpoint,
            [CreateComponentProjection(
                processorId,
                key,
                source.Checkpoint,
                0.50m,
                "old-publication")]));
        var oldState = await ReadDurableStateAsync(processorId);

        var nextRevision = Advance(source.Checkpoint);
        var advance = CreateAdvanceCommit(
            processorId,
            source.Checkpoint,
            [key],
            nextRevision,
            [CreateComponentProjection(
                processorId,
                key,
                nextRevision,
                0.80m,
                "new-publication")]);

        var writerPaused = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWriter = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new SqlServerOperationalMetricProjectionCommitTransaction(
            _fixture.ConnectionString);
        var writerTask = writer.ExecuteAsync(
            advance,
            async (context, cancellationToken) =>
            {
                await SqlServerOperationalMetricProjectionPublication.ExecuteAsync(
                    context,
                    advance,
                    async (stage, stageCancellationToken) =>
                    {
                        if (stage == SqlServerOperationalMetricProjectionPublicationStage.EvidenceDeleted)
                        {
                            writerPaused.TrySetResult(true);
                            await releaseWriter.Task.WaitAsync(stageCancellationToken);
                        }
                    },
                    cancellationToken);
            },
            CancellationToken.None);

        await writerPaused.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var firstCheckpointRead = new TaskCompletionSource<ulong>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var readerTask = ReadCheckpointStablePublicationAsync(
            processorId,
            firstCheckpointRead,
            CancellationToken.None);
        var firstObservedCheckpoint = await firstCheckpointRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(oldState.CheckpointPosition, firstObservedCheckpoint);

        releaseWriter.TrySetResult(true);
        await writerTask;

        var readerResult = await readerTask.WaitAsync(TimeSpan.FromSeconds(10));
        var newState = await ReadDurableStateAsync(processorId);

        Assert.True(readerResult.RetryCount >= 1);
        Assert.Equal(newState, readerResult.State);
        Assert.NotEqual(oldState, readerResult.State);
        Assert.Equal(nextRevision.Position.Value, readerResult.State.CheckpointPosition);
        Assert.Contains("new-publication", readerResult.State.EvidenceJson, StringComparison.Ordinal);
        Assert.DoesNotContain("old-publication", readerResult.State.EvidenceJson, StringComparison.Ordinal);
    }

    private async Task<CheckpointStableReadResult> ReadCheckpointStablePublicationAsync(
        OperationalMetricProjectionProcessorId processorId,
        TaskCompletionSource<ulong> firstCheckpointRead,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await using var connection = _fixture.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var processorRowId = await ReadProcessorRowIdAsync(
                connection,
                processorId,
                cancellationToken);
            var checkpoint1 = await ReadCheckpointPositionAsync(
                connection,
                processorRowId,
                cancellationToken);
            if (attempt == 0)
            {
                firstCheckpointRead.TrySetResult(checkpoint1);
            }

            var state = await ReadDurableStateOnConnectionAsync(
                connection,
                processorRowId,
                checkpoint1,
                cancellationToken);
            var checkpoint2 = await ReadCheckpointPositionAsync(
                connection,
                processorRowId,
                cancellationToken);
            if (checkpoint1 == checkpoint2)
            {
                return new CheckpointStableReadResult(
                    state with { CheckpointPosition = checkpoint2 },
                    attempt);
            }
        }

        throw new Xunit.Sdk.XunitException(
            "Checkpoint-stability reader did not converge to a complete publication.");
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
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        var processorRowId = await ReadProcessorRowIdAsync(
            connection,
            processorId,
            CancellationToken.None);
        var checkpoint = await ReadCheckpointPositionAsync(
            connection,
            processorRowId,
            CancellationToken.None);
        return await ReadDurableStateOnConnectionAsync(
            connection,
            processorRowId,
            checkpoint,
            CancellationToken.None);
    }

    private static async Task<DurablePublicationState> ReadDurableStateOnConnectionAsync(
        SqlConnection connection,
        long processorRowId,
        ulong checkpointPosition,
        CancellationToken cancellationToken)
    {
        var projectionsJson = await ReadJsonAsync(
            connection,
            """
            SELECT p.*
            FROM dbo.OperationalMetricProjection AS p
            WHERE p.OperationalMetricProjectionProcessorRowId = @ProcessorRowId
            ORDER BY p.OperationalMetricProjectionRowId
            FOR JSON PATH, INCLUDE_NULL_VALUES;
            """,
            processorRowId,
            cancellationToken);
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
            processorRowId,
            cancellationToken);
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
            processorRowId,
            cancellationToken);
        return new DurablePublicationState(
            checkpointPosition,
            projectionsJson,
            manifestJson,
            evidenceJson);
    }

    private static async Task<long> ReadProcessorRowIdAsync(
        SqlConnection connection,
        OperationalMetricProjectionProcessorId processorId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT OperationalMetricProjectionProcessorRowId
            FROM dbo.OperationalMetricProjectionProcessor
            WHERE ProcessorKey = @ProcessorKey;
            """;
        command.Parameters.Add("@ProcessorKey", SqlDbType.NVarChar, 256).Value = processorId.Value;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<ulong> ReadCheckpointPositionAsync(
        SqlConnection connection,
        long processorRowId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Position
            FROM dbo.OperationalMetricProjectionCheckpoint
            WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId;
            """;
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
        return SqlServerUInt64.Materialize(
            Convert.ToDecimal(
                await command.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture));
    }

    private static async Task<string> ReadJsonAsync(
        SqlConnection connection,
        string sql,
        long processorRowId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
        return Convert.ToString(
                   await command.ExecuteScalarAsync(cancellationToken),
                   System.Globalization.CultureInfo.InvariantCulture)
               ?? string.Empty;
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
            null,
            null,
            revision,
            [evidence],
            null);
    }

    private static OperationalMetricProjectionCommit CreateInitialCommit(
        OperationalMetricProjectionProcessorId processorId,
        MetricAggregationCheckpoint revision,
        IReadOnlyList<OperationalMetricProjection> projections) =>
        new(
            processorId,
            null,
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

    private static OperationalMetricProjectionCommit CreateReplayCommit(
        OperationalMetricProjectionProcessorId processorId,
        MetricAggregationCheckpoint revision,
        IReadOnlyList<OperationalMetricProjection> projections) =>
        new(
            processorId,
            null,
            new OperationalMetricProjectionCheckpoint(
                processorId,
                revision,
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
            CreateAppend(machineId, $"joint-publication-source-{Guid.NewGuid():N}"),
            CancellationToken.None);
        var processorId = new MetricAggregationProcessorId(
            $"joint-publication-source-{Guid.NewGuid():N}");
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
            new ShiftOccurrenceId(siteId, scheduleId, shiftId, start, start.AddHours(8)),
            new ProductionDayId(siteId, DateOnly.FromDateTime(start.UtcDateTime)));
    }

    private static OperationalMetricProjectionProcessorId NewProcessorId(string suffix) =>
        new($"joint-publication-{suffix}-{Guid.NewGuid():N}");

    private sealed record SourceFixture(
        MachineId MachineId,
        MetricAggregationCheckpoint Checkpoint);

    private sealed record DurablePublicationState(
        ulong CheckpointPosition,
        string ProjectionsJson,
        string ManifestJson,
        string EvidenceJson);

    private sealed record CheckpointStableReadResult(
        DurablePublicationState State,
        int RetryCount);

    private sealed class InjectedPublicationFailureException : Exception
    {
    }
}
