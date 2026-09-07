using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerOperationalMetricProjectionPublicationIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerOperationalMetricProjectionPublicationIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EmptyToPopulatedPublishesProjectionManifestEvidenceAndCheckpoint()
    {
        var source = await CreateSourceAsync();
        var processorId = NewProcessorId();
        var key = CreateShiftKey(source.MachineId, "availability");
        var projection = CreateComponentProjection(processorId, key, source.Checkpoint, 0.5m, "runtime-a");
        var commit = CreateInitialCommit(processorId, source.Checkpoint, [projection]);

        await ExecutePublicationAsync(commit);

        var state = await ReadStateAsync(processorId);
        Assert.Single(state.ProjectionRows);
        Assert.Single(state.ManifestRowIds);
        Assert.Equal(state.ProjectionRows[0].RowId, state.ManifestRowIds[0]);
        Assert.Single(state.EvidenceRows);
        Assert.Equal((byte)1, state.EvidenceRows[0].Kind);
        Assert.Equal("runtime-a", state.EvidenceRows[0].OperandName);
        Assert.Equal(source.Checkpoint.Position.Value, state.CheckpointPosition);
    }

    [Fact]
    public async Task RetainedRowReplacementPreservesIdentityAndReplacesEvidence()
    {
        var source = await CreateSourceAsync();
        var processorId = NewProcessorId();
        var key = CreateShiftKey(source.MachineId, "availability");
        var initialProjection = CreateComponentProjection(processorId, key, source.Checkpoint, 0.5m, "runtime-a");
        await ExecutePublicationAsync(CreateInitialCommit(processorId, source.Checkpoint, [initialProjection]));
        var before = await ReadStateAsync(processorId);
        var retainedRowId = Assert.Single(before.ProjectionRows).RowId;

        var nextRevision = Advance(source.Checkpoint);
        var replacement = CreateComponentProjection(processorId, key, nextRevision, 0.75m, "runtime-b");
        await ExecutePublicationAsync(CreateAdvanceCommit(
            processorId,
            source.Checkpoint,
            [key],
            nextRevision,
            [replacement]));

        var after = await ReadStateAsync(processorId);
        var row = Assert.Single(after.ProjectionRows);
        Assert.Equal(retainedRowId, row.RowId);
        Assert.Equal("0.75", row.MetricValue);
        Assert.Equal(nextRevision.Position.Value, row.SourceRevisionPosition);
        Assert.Equal([retainedRowId], after.ManifestRowIds);
        var evidence = Assert.Single(after.EvidenceRows);
        Assert.Equal((byte)1, evidence.Kind);
        Assert.Equal("runtime-b", evidence.OperandName);
        Assert.Equal(nextRevision.Position.Value, after.CheckpointPosition);
    }

    [Fact]
    public async Task ManifestGrowthRetainsExistingMembershipAndAddsNewProjection()
    {
        var source = await CreateSourceAsync();
        var processorId = NewProcessorId();
        var keyA = CreateShiftKey(source.MachineId, "availability");
        var initial = CreateCalculated(processorId, keyA, source.Checkpoint, 0.5m);
        await ExecutePublicationAsync(CreateInitialCommit(processorId, source.Checkpoint, [initial]));
        var before = await ReadStateAsync(processorId);
        var retainedRowId = Assert.Single(before.ProjectionRows).RowId;

        var nextRevision = Advance(source.Checkpoint);
        var keyB = CreateShiftKey(source.MachineId, "performance");
        var retained = CreateCalculated(processorId, keyA, nextRevision, 0.6m);
        var added = CreateCalculated(processorId, keyB, nextRevision, 0.8m);
        await ExecutePublicationAsync(CreateAdvanceCommit(
            processorId,
            source.Checkpoint,
            [keyA],
            nextRevision,
            [retained, added]));

        var after = await ReadStateAsync(processorId);
        Assert.Equal(2, after.ProjectionRows.Count);
        Assert.Equal(2, after.ManifestRowIds.Count);
        Assert.Contains(retainedRowId, after.ManifestRowIds);
        Assert.Equal(
            after.ProjectionRows.Select(static row => row.RowId).Order(),
            after.ManifestRowIds.Order());
    }

    [Fact]
    public async Task ManifestShrinkageDeletesObsoleteProjectionAndKeepsRetainedMembership()
    {
        var source = await CreateSourceAsync();
        var processorId = NewProcessorId();
        var keyA = CreateShiftKey(source.MachineId, "availability");
        var keyB = CreateShiftKey(source.MachineId, "performance");
        await ExecutePublicationAsync(CreateInitialCommit(
            processorId,
            source.Checkpoint,
            [
                CreateCalculated(processorId, keyA, source.Checkpoint, 0.5m),
                CreateCalculated(processorId, keyB, source.Checkpoint, 0.8m),
            ]));
        var before = await ReadStateAsync(processorId);
        Assert.Equal(2, before.ProjectionRows.Count);
        var retainedBefore = before.ProjectionRows.Single(row => row.MetricKey == "availability").RowId;

        var nextRevision = Advance(source.Checkpoint);
        await ExecutePublicationAsync(CreateAdvanceCommit(
            processorId,
            source.Checkpoint,
            [keyA, keyB],
            nextRevision,
            [CreateCalculated(processorId, keyA, nextRevision, 0.65m)]));

        var after = await ReadStateAsync(processorId);
        var retainedAfter = Assert.Single(after.ProjectionRows);
        Assert.Equal(retainedBefore, retainedAfter.RowId);
        Assert.Equal("availability", retainedAfter.MetricKey);
        Assert.Equal([retainedBefore], after.ManifestRowIds);
    }

    [Fact]
    public async Task NonemptyToEmptyDeletesCompletePublishedSet()
    {
        var source = await CreateSourceAsync();
        var processorId = NewProcessorId();
        var key = CreateShiftKey(source.MachineId, "availability");
        await ExecutePublicationAsync(CreateInitialCommit(
            processorId,
            source.Checkpoint,
            [CreateComponentProjection(processorId, key, source.Checkpoint, 0.5m, "runtime")]));

        var nextRevision = Advance(source.Checkpoint);
        await ExecutePublicationAsync(CreateAdvanceCommit(
            processorId,
            source.Checkpoint,
            [key],
            nextRevision,
            []));

        var after = await ReadStateAsync(processorId);
        Assert.Empty(after.ProjectionRows);
        Assert.Empty(after.ManifestRowIds);
        Assert.Empty(after.EvidenceRows);
        Assert.Equal(nextRevision.Position.Value, after.CheckpointPosition);
    }

    [Fact]
    public async Task ComponentEvidenceCanBeReplacedByDependencyEvidenceOnRetainedProjection()
    {
        var source = await CreateSourceAsync();
        var processorId = NewProcessorId();
        var parentKey = CreateShiftKey(source.MachineId, "oee");
        var initial = CreateComponentProjection(processorId, parentKey, source.Checkpoint, 0.5m, "runtime");
        await ExecutePublicationAsync(CreateInitialCommit(processorId, source.Checkpoint, [initial]));
        var before = await ReadStateAsync(processorId);
        var retainedRowId = Assert.Single(before.ProjectionRows).RowId;
        Assert.Equal((byte)1, Assert.Single(before.EvidenceRows).Kind);

        var nextRevision = Advance(source.Checkpoint);
        var dependencyKey = CreateShiftKey(source.MachineId, "availability");
        var dependencyProjection = CreateCalculated(processorId, dependencyKey, nextRevision, 0.9m);
        var dependencyEvidence = new OperationalMetricDependencyProjectionEvidence(
            "availability-dependency",
            dependencyKey.DefinitionId,
            dependencyProjection);
        var replacement = new OperationalMetricProjection(
            processorId,
            parentKey,
            OperationalMetricEvaluationStatus.Calculated,
            0.7m,
            "ratio",
            null,
            null,
            nextRevision,
            operandEvidence: null,
            dependencyEvidence: [dependencyEvidence]);

        await ExecutePublicationAsync(CreateAdvanceCommit(
            processorId,
            source.Checkpoint,
            [parentKey],
            nextRevision,
            [replacement]));

        var after = await ReadStateAsync(processorId);
        Assert.Equal(retainedRowId, Assert.Single(after.ProjectionRows).RowId);
        var evidence = Assert.Single(after.EvidenceRows);
        Assert.Equal((byte)2, evidence.Kind);
        Assert.Equal("availability-dependency", evidence.OperandName);
        Assert.Equal("availability", evidence.DependencyMetricKey);
        Assert.Equal((short)1, evidence.DependencySnapshotCodecVersion);
        Assert.NotNull(evidence.DependencySnapshotHash);
        Assert.Equal(32, evidence.DependencySnapshotHash!.Length);
        Assert.NotNull(evidence.DependencySnapshotBinary);
        Assert.True(evidence.DependencySnapshotBinary!.Length > 7);
        Assert.Equal("FCDS", System.Text.Encoding.ASCII.GetString(evidence.DependencySnapshotBinary, 0, 4));
    }

    [Fact]
    public async Task ReconcileProposedValidatesExactStateWithoutCallingMutationRoutine()
    {
        var source = await CreateSourceAsync();
        var processorId = NewProcessorId();
        var key = CreateShiftKey(source.MachineId, "availability");
        var projection = CreateComponentProjection(processorId, key, source.Checkpoint, 0.5m, "runtime");
        await ExecutePublicationAsync(CreateInitialCommit(processorId, source.Checkpoint, [projection]));
        var before = await ReadStateAsync(processorId);

        var replay = CreateReplayCommit(processorId, source.Checkpoint, [projection]);
        var transaction = new SqlServerOperationalMetricProjectionCommitTransaction(_fixture.ConnectionString);
        await transaction.ExecuteAsync(
            replay,
            async (context, cancellationToken) =>
            {
                Assert.Equal(SqlServerOperationalMetricProjectionCommitMode.ReconcileProposed, context.Mode);
                await SqlServerOperationalMetricProjectionPublication.ExecuteAsync(
                    context,
                    replay,
                    static (_, _) => throw new Xunit.Sdk.XunitException(
                        "ReconcileProposed entered the advancing mutation routine."),
                    cancellationToken);
            },
            CancellationToken.None);

        var after = await ReadStateAsync(processorId);
        Assert.Equal(before.CheckpointPosition, after.CheckpointPosition);
        Assert.Equal(before.ProjectionRows, after.ProjectionRows);
        Assert.Equal(before.ManifestRowIds, after.ManifestRowIds);

        Assert.Equal(before.EvidenceRows.Count, after.EvidenceRows.Count);
        for (var index = 0; index < before.EvidenceRows.Count; index++)
        {
            var expected = before.EvidenceRows[index];
            var actual = after.EvidenceRows[index];

            Assert.Equal(expected.ProjectionRowId, actual.ProjectionRowId);
            Assert.Equal(expected.Kind, actual.Kind);
            Assert.Equal(expected.Ordinal, actual.Ordinal);
            Assert.Equal(expected.OperandName, actual.OperandName);
            Assert.Equal(expected.DependencyMetricKey, actual.DependencyMetricKey);
            Assert.Equal(
                expected.DependencySnapshotCodecVersion,
                actual.DependencySnapshotCodecVersion);
            Assert.Equal(
                expected.DependencySnapshotHash,
                actual.DependencySnapshotHash);
            Assert.Equal(
                expected.DependencySnapshotBinary,
                actual.DependencySnapshotBinary);
        }
    }

    private async Task ExecutePublicationAsync(OperationalMetricProjectionCommit commit)
    {
        var transaction = new SqlServerOperationalMetricProjectionCommitTransaction(_fixture.ConnectionString);
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

    private async Task<PublicationState> ReadStateAsync(
        OperationalMetricProjectionProcessorId processorId)
    {
        var transaction = new SqlServerOperationalMetricProjectionCommitTransaction(_fixture.ConnectionString);
        var header = await transaction.ReadCheckpointHeaderAsync(processorId, CancellationToken.None);
        Assert.NotNull(header);

        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();

        var projections = new List<ProjectionState>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT
                    OperationalMetricProjectionRowId,
                    MetricKey,
                    MetricValue,
                    SourceRevisionPosition
                FROM dbo.OperationalMetricProjection
                WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId
                ORDER BY OperationalMetricProjectionRowId;
                """;
            command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = header.ProjectionProcessorRowId;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                projections.Add(new ProjectionState(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    SqlServerUInt64.Materialize(reader.GetDecimal(3))));
            }
        }

        var manifest = new List<long>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT OperationalMetricProjectionRowId
                FROM dbo.OperationalMetricProjectionManifest
                WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId
                ORDER BY OperationalMetricProjectionRowId;
                """;
            command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = header.ProjectionProcessorRowId;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                manifest.Add(reader.GetInt64(0));
            }
        }

        var evidence = new List<EvidenceState>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT
                    e.OperationalMetricProjectionRowId,
                    e.EvidenceKind,
                    e.EvidenceOrdinal,
                    e.OperandName,
                    e.DependencyMetricKey,
                    e.DependencySnapshotCodecVersion,
                    e.DependencySnapshotHash,
                    e.DependencySnapshotBinary
                FROM dbo.OperationalMetricProjectionEvidence AS e
                INNER JOIN dbo.OperationalMetricProjection AS p
                    ON p.OperationalMetricProjectionRowId = e.OperationalMetricProjectionRowId
                WHERE p.OperationalMetricProjectionProcessorRowId = @ProcessorRowId
                ORDER BY e.OperationalMetricProjectionRowId, e.EvidenceKind, e.EvidenceOrdinal;
                """;
            command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = header.ProjectionProcessorRowId;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                evidence.Add(new EvidenceState(
                    reader.GetInt64(0),
                    reader.GetByte(1),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetInt16(5),
                    reader.IsDBNull(6) ? null : (byte[])reader[6],
                    reader.IsDBNull(7) ? null : (byte[])reader[7]));
            }
        }

        return new PublicationState(
            projections.ToArray(),
            manifest.ToArray(),
            evidence.ToArray(),
            header.Position.Value);
    }

    private static OperationalMetricProjection CreateCalculated(
        OperationalMetricProjectionProcessorId processorId,
        OperationalMetricEvaluationKey key,
        MetricAggregationCheckpoint revision,
        decimal value) =>
        new(
            processorId,
            key,
            OperationalMetricEvaluationStatus.Calculated,
            value,
            "ratio",
            null,
            null,
            revision);

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
            new DateTimeOffset(2026, 9, 7, 6, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 7, 14, 0, 0, TimeSpan.Zero));
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
            CreateAppend(machineId, $"publication-dml-source-{Guid.NewGuid():N}"),
            CancellationToken.None);
        var processorId = new MetricAggregationProcessorId(
            $"publication-dml-source-{Guid.NewGuid():N}");
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
            new ShiftOccurrenceId(siteId, scheduleId, shiftId, start, start.AddHours(8)),
            new ProductionDayId(siteId, DateOnly.FromDateTime(start.UtcDateTime)));
    }

    private static OperationalMetricProjectionProcessorId NewProcessorId() =>
        new($"publication-dml-{Guid.NewGuid():N}");

    private sealed record SourceFixture(
        MachineId MachineId,
        MetricAggregationCheckpoint Checkpoint);

    private sealed record ProjectionState(
        long RowId,
        string MetricKey,
        string? MetricValue,
        ulong SourceRevisionPosition);

    private sealed record EvidenceState(
        long ProjectionRowId,
        byte Kind,
        int Ordinal,
        string OperandName,
        string? DependencyMetricKey,
        short? DependencySnapshotCodecVersion,
        byte[]? DependencySnapshotHash,
        byte[]? DependencySnapshotBinary);

    private sealed record PublicationState(
        IReadOnlyList<ProjectionState> ProjectionRows,
        IReadOnlyList<long> ManifestRowIds,
        IReadOnlyList<EvidenceState> EvidenceRows,
        ulong CheckpointPosition);
}
