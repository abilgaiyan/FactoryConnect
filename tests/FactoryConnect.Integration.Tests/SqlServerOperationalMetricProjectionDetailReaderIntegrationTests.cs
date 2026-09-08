using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerOperationalMetricProjectionDetailReaderIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerOperationalMetricProjectionDetailReaderIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ReadDetailAsyncReconstructsExactManifestedProjectionWithCompleteEvidence()
    {
        var source = await CreateSourceAsync();
        var processorId = NewProcessorId();
        var period = CreateShiftPeriod();
        var context = OperationalMetricEvaluationContextKey.Unpartitioned;

        var dependencyKey = CreateKey(source.MachineId, period, "availability", "1", context);
        var dependency = CreateCalculated(processorId, dependencyKey, source.Checkpoint, 0.75m);

        var component = new OperationalMetricComponentProjectionEvidence(
            "runtime",
            new OperationalMetricAggregateSourceIdentity(
                source.Checkpoint.ProcessorId,
                source.MachineId,
                period,
                "runtime-seconds"),
            source.Checkpoint,
            MetricDimension.Duration,
            1800m,
            "seconds",
            3,
            new DateTimeOffset(2026, 9, 7, 6, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 7, 6, 30, 0, TimeSpan.Zero));

        var dependencyEvidence = new OperationalMetricDependencyProjectionEvidence(
            "availability",
            dependencyKey.DefinitionId,
            dependency);

        var parentKey = CreateKey(source.MachineId, period, "oee", "1", context);
        var parent = new OperationalMetricProjection(
            processorId,
            parentKey,
            OperationalMetricEvaluationStatus.Calculated,
            0.625m,
            "ratio",
            null,
            null,
            source.Checkpoint,
            [component],
            [dependencyEvidence]);

        await PublishAsync(CreateInitialCommit(
            processorId,
            source.Checkpoint,
            [dependency, parent]));

        var reader = new SqlServerOperationalMetricProjectionQueryReader(_fixture.ConnectionString);
        var actual = await reader.ReadDetailAsync(
            processorId,
            parentKey,
            CancellationToken.None);

        var projection = Assert.IsType<OperationalMetricProjection>(actual);
        Assert.Equal(parent.ProcessorId, projection.ProcessorId);
        Assert.Equal(parent.Key, projection.Key);
        Assert.Equal(parent.Status, projection.Status);
        Assert.Equal(parent.Value, projection.Value);
        Assert.Equal(parent.Unit, projection.Unit);
        Assert.Equal(parent.SourceRevision, projection.SourceRevision);

        var actualComponent = Assert.Single(projection.OperandEvidence);
        Assert.Equal(component, actualComponent);

        var actualDependency = Assert.Single(projection.DependencyEvidence);
        Assert.Equal(dependencyEvidence.OperandName, actualDependency.OperandName);
        Assert.Equal(dependencyEvidence.DefinitionId, actualDependency.DefinitionId);
        AssertProjectionSemanticsEqual(dependency, actualDependency.Projection);
    }

    [Fact]
    public async Task ReadDetailAsyncReturnsNullWhenKeyIsAbsentFromCurrentManifestEvenIfPhysicalRowExists()
    {
        var source = await CreateSourceAsync();
        var processorId = NewProcessorId();
        var period = CreateShiftPeriod();
        var context = OperationalMetricEvaluationContextKey.Unpartitioned;
        var visibleKey = CreateKey(source.MachineId, period, "availability", "1", context);
        var staleKey = CreateKey(source.MachineId, period, "performance", "1", context);

        await PublishAsync(CreateInitialCommit(
            processorId,
            source.Checkpoint,
            [
                CreateCalculated(processorId, visibleKey, source.Checkpoint, 0.5m),
                CreateCalculated(processorId, staleKey, source.Checkpoint, 0.8m),
            ]));

        var header = await ReadCheckpointHeaderAsync(processorId);
        await using (var connection = _fixture.CreateConnection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE m
                FROM dbo.OperationalMetricProjectionManifest AS m
                INNER JOIN dbo.OperationalMetricProjection AS p
                    ON p.OperationalMetricProjectionProcessorRowId = m.OperationalMetricProjectionProcessorRowId
                    AND p.OperationalMetricProjectionRowId = m.OperationalMetricProjectionRowId
                WHERE m.OperationalMetricProjectionProcessorRowId = @ProcessorRowId
                    AND p.MetricKey = N'performance';
                """;
            command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = header.ProjectionProcessorRowId;
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var reader = new SqlServerOperationalMetricProjectionQueryReader(_fixture.ConnectionString);
        var detail = await reader.ReadDetailAsync(
            processorId,
            staleKey,
            CancellationToken.None);

        Assert.Null(detail);
    }

    [Fact]
    public async Task ReadDetailAsyncReturnsNullWhenRequestedKeyWasNeverPublished()
    {
        var source = await CreateSourceAsync();
        var processorId = NewProcessorId();
        var period = CreateShiftPeriod();
        var context = OperationalMetricEvaluationContextKey.Unpartitioned;
        var publishedKey = CreateKey(source.MachineId, period, "availability", "1", context);
        var absentKey = CreateKey(source.MachineId, period, "performance", "1", context);

        await PublishAsync(CreateInitialCommit(
            processorId,
            source.Checkpoint,
            [CreateCalculated(processorId, publishedKey, source.Checkpoint, 0.5m)]));

        var reader = new SqlServerOperationalMetricProjectionQueryReader(_fixture.ConnectionString);
        var detail = await reader.ReadDetailAsync(
            processorId,
            absentKey,
            CancellationToken.None);

        Assert.Null(detail);
    }

    [Fact]
    public async Task ReadDetailAsyncFailsWhenManifestedEvidenceCanonicalStateIsCorrupt()
    {
        var source = await CreateSourceAsync();
        var processorId = NewProcessorId();
        var period = CreateShiftPeriod();
        var context = OperationalMetricEvaluationContextKey.Unpartitioned;
        var key = CreateKey(source.MachineId, period, "availability", "1", context);

        var component = new OperationalMetricComponentProjectionEvidence(
            "runtime",
            new OperationalMetricAggregateSourceIdentity(
                source.Checkpoint.ProcessorId,
                source.MachineId,
                period,
                "runtime-seconds"),
            source.Checkpoint,
            MetricDimension.Duration,
            1800m,
            "seconds",
            3,
            new DateTimeOffset(2026, 9, 7, 6, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 7, 6, 30, 0, TimeSpan.Zero));

        var projection = new OperationalMetricProjection(
            processorId,
            key,
            OperationalMetricEvaluationStatus.Calculated,
            0.5m,
            "ratio",
            null,
            null,
            source.Checkpoint,
            [component]);

        await PublishAsync(CreateInitialCommit(processorId, source.Checkpoint, [projection]));

        var header = await ReadCheckpointHeaderAsync(processorId);
        await using (var connection = _fixture.CreateConnection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE e
                SET OperandNameOrderKey = 0x00
                FROM dbo.OperationalMetricProjectionEvidence AS e
                INNER JOIN dbo.OperationalMetricProjection AS p
                    ON p.OperationalMetricProjectionRowId = e.OperationalMetricProjectionRowId
                WHERE p.OperationalMetricProjectionProcessorRowId = @ProcessorRowId
                    AND p.MetricKey = N'availability';
                """;
            command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = header.ProjectionProcessorRowId;
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var reader = new SqlServerOperationalMetricProjectionQueryReader(_fixture.ConnectionString);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await reader.ReadDetailAsync(
                processorId,
                key,
                CancellationToken.None));

        Assert.Contains("corrupt", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertProjectionSemanticsEqual(
        OperationalMetricProjection expected,
        OperationalMetricProjection actual)
    {
        Assert.Equal(expected.ProcessorId, actual.ProcessorId);
        Assert.Equal(expected.Key, actual.Key);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.Value, actual.Value);
        Assert.Equal(expected.Unit, actual.Unit);
        Assert.Equal(expected.ReasonCode, actual.ReasonCode);
        Assert.Equal(expected.ReasonOperandName, actual.ReasonOperandName);
        Assert.Equal(expected.SourceRevision, actual.SourceRevision);
        Assert.Equal(expected.OperandEvidence, actual.OperandEvidence);
        Assert.Equal(expected.DependencyEvidence.Count, actual.DependencyEvidence.Count);

        for (var index = 0; index < expected.DependencyEvidence.Count; index++)
        {
            var expectedDependency = expected.DependencyEvidence[index];
            var actualDependency = actual.DependencyEvidence[index];
            Assert.Equal(expectedDependency.OperandName, actualDependency.OperandName);
            Assert.Equal(expectedDependency.DefinitionId, actualDependency.DefinitionId);
            AssertProjectionSemanticsEqual(expectedDependency.Projection, actualDependency.Projection);
        }
    }

    private async Task PublishAsync(OperationalMetricProjectionCommit commit)
    {
        var transaction = new SqlServerOperationalMetricProjectionCommitTransaction(_fixture.ConnectionString);
        await transaction.ExecuteAsync(
            commit,
            async (context, cancellationToken) =>
                await SqlServerOperationalMetricProjectionPublication.ExecuteAsync(
                    context,
                    commit,
                    cancellationToken),
            CancellationToken.None);
    }

    private async Task<SqlServerOperationalMetricProjectionCheckpointHeader> ReadCheckpointHeaderAsync(
        OperationalMetricProjectionProcessorId processorId)
    {
        var transaction = new SqlServerOperationalMetricProjectionCommitTransaction(_fixture.ConnectionString);
        var header = await transaction.ReadCheckpointHeaderAsync(processorId, CancellationToken.None);
        return Assert.IsType<SqlServerOperationalMetricProjectionCheckpointHeader>(header);
    }

    private async Task<SourceFixture> CreateSourceAsync()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var fact = await inputStore.AppendAsync(
            CreateAppend(machineId, $"detail-reader-source-{Guid.NewGuid():N}"),
            CancellationToken.None);
        var aggregationProcessorId = new MetricAggregationProcessorId(
            $"detail-reader-aggregation-{Guid.NewGuid():N}");
        var checkpoint = new MetricAggregationCheckpoint(
            aggregationProcessorId,
            fact.StreamId,
            fact.Position);
        var aggregationStore = new SqlServerMetricAggregationStore(_fixture.ConnectionString);
        await aggregationStore.CommitAsync(
            new MetricAggregationCommit(aggregationProcessorId, null, checkpoint, []),
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
            EndsAtUtc = start.AddSeconds(1),
            CompanyId = new CompanyId("COMPANY-1"),
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

    private static OperationalMetricPeriodId.Shift CreateShiftPeriod() =>
        new(
            new ShiftOccurrenceId(
                new SiteId("SITE-1"),
                new ShiftScheduleAssignmentId("SCHEDULE-A"),
                new ShiftId("SHIFT-A"),
                new DateTimeOffset(2026, 9, 7, 6, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 9, 7, 14, 0, 0, TimeSpan.Zero)));

    private static OperationalMetricEvaluationKey CreateKey(
        MachineId machineId,
        OperationalMetricPeriodId periodId,
        string metricKey,
        string version,
        OperationalMetricEvaluationContextKey contextKey) =>
        new(
            machineId,
            periodId,
            new OperationalMetricDefinitionId(metricKey, version),
            contextKey);

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

    private static OperationalMetricProjectionProcessorId NewProcessorId() =>
        new($"detail-reader-{Guid.NewGuid():N}");

    private sealed record SourceFixture(
        MachineId MachineId,
        MetricAggregationCheckpoint Checkpoint);
}
