using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerOperationalMetricProjectionPublicationReaderConformanceIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerOperationalMetricProjectionPublicationReaderConformanceIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PublishedProjectionIsReadAsOneCoherentCommittedSnapshot()
    {
        var source = await CreateSourceAsync();
        var processorId = NewProcessorId();
        var period = CreateShiftPeriod();
        var context = OperationalMetricEvaluationContextKey.Unpartitioned;
        var key = CreateKey(source.MachineId, period, "availability", context);
        var expected = CreateComponentProjection(
            processorId,
            key,
            source.Checkpoint,
            0.5m,
            "runtime-initial");

        await PublishAsync(CreateInitialCommit(
            processorId,
            source.Checkpoint,
            [expected]));

        var summaryReader = new SqlServerOperationalMetricProjectionSummaryReader(
            _fixture.ConnectionString);
        var summaries = await summaryReader.ReadPeriodSummariesAsync(
            processorId,
            source.MachineId,
            period,
            context,
            CancellationToken.None);

        var summary = Assert.Single(summaries);
        Assert.Equal(expected.ProcessorId, summary.ProcessorId);
        Assert.Equal(expected.Key, summary.Key);
        Assert.Equal(expected.Status, summary.Status);
        Assert.Equal(expected.Value, summary.Value);
        Assert.Equal(expected.Unit, summary.Unit);
        Assert.Equal(expected.ReasonCode, summary.ReasonCode);
        Assert.Equal(expected.ReasonOperandName, summary.ReasonOperandName);
        Assert.Equal(expected.SourceRevision, summary.SourceRevision);

        var detailReader = new SqlServerOperationalMetricProjectionQueryReader(
            _fixture.ConnectionString);
        var detail = await detailReader.ReadDetailAsync(
            processorId,
            key,
            CancellationToken.None);

        var projection = Assert.IsType<OperationalMetricProjection>(detail);
        AssertProjectionSemanticsEqual(expected, projection);
        Assert.Equal(
            "runtime-initial",
            Assert.Single(projection.OperandEvidence).OperandName);
    }

    [Fact]
    public async Task AdvancingPublicationReplacesReaderVisibleSnapshotAtomically()
    {
        var source = await CreateSourceAsync();
        var processorId = NewProcessorId();
        var period = CreateShiftPeriod();
        var context = OperationalMetricEvaluationContextKey.Unpartitioned;
        var availabilityKey = CreateKey(source.MachineId, period, "availability", context);
        var performanceKey = CreateKey(source.MachineId, period, "performance", context);

        var initialAvailability = CreateComponentProjection(
            processorId,
            availabilityKey,
            source.Checkpoint,
            0.5m,
            "runtime-old");
        var initialPerformance = CreateCalculated(
            processorId,
            performanceKey,
            source.Checkpoint,
            0.8m);

        await PublishAsync(CreateInitialCommit(
            processorId,
            source.Checkpoint,
            [initialAvailability, initialPerformance]));

        var nextRevision = await AdvanceSourceAsync(source);
        var qualityKey = CreateKey(source.MachineId, period, "quality", context);
        var replacementAvailability = CreateComponentProjection(
            processorId,
            availabilityKey,
            nextRevision,
            0.75m,
            "runtime-new");
        var addedQuality = CreateCalculated(
            processorId,
            qualityKey,
            nextRevision,
            0.95m);

        await PublishAsync(CreateAdvanceCommit(
            processorId,
            source.Checkpoint,
            [availabilityKey, performanceKey],
            nextRevision,
            [replacementAvailability, addedQuality]));

        var summaryReader = new SqlServerOperationalMetricProjectionSummaryReader(
            _fixture.ConnectionString);
        var summaries = await summaryReader.ReadPeriodSummariesAsync(
            processorId,
            source.MachineId,
            period,
            context,
            CancellationToken.None);

        Assert.Equal(2, summaries.Count);
        Assert.Equal(
            ["availability", "quality"],
            summaries.Select(static summary => summary.Key.DefinitionId.MetricKey));
        Assert.All(
            summaries,
            summary => Assert.Equal(nextRevision, summary.SourceRevision));

        var availabilitySummary = summaries[0];
        Assert.Equal(0.75m, availabilitySummary.Value);
        var qualitySummary = summaries[1];
        Assert.Equal(0.95m, qualitySummary.Value);

        var detailReader = new SqlServerOperationalMetricProjectionQueryReader(
            _fixture.ConnectionString);

        var availabilityDetail = Assert.IsType<OperationalMetricProjection>(
            await detailReader.ReadDetailAsync(
                processorId,
                availabilityKey,
                CancellationToken.None));
        AssertProjectionSemanticsEqual(replacementAvailability, availabilityDetail);
        Assert.Equal(
            "runtime-new",
            Assert.Single(availabilityDetail.OperandEvidence).OperandName);
        Assert.DoesNotContain(
            availabilityDetail.OperandEvidence,
            static evidence => evidence.OperandName == "runtime-old");

        var qualityDetail = Assert.IsType<OperationalMetricProjection>(
            await detailReader.ReadDetailAsync(
                processorId,
                qualityKey,
                CancellationToken.None));
        AssertProjectionSemanticsEqual(addedQuality, qualityDetail);

        var removedDetail = await detailReader.ReadDetailAsync(
            processorId,
            performanceKey,
            CancellationToken.None);
        Assert.Null(removedDetail);
    }

    private async Task PublishAsync(OperationalMetricProjectionCommit commit)
    {
        var transaction = new SqlServerOperationalMetricProjectionCommitTransaction(
            _fixture.ConnectionString);
        await transaction.ExecuteAsync(
            commit,
            async (context, cancellationToken) =>
                await SqlServerOperationalMetricProjectionPublication.ExecuteAsync(
                    context,
                    commit,
                    cancellationToken),
            CancellationToken.None);
    }

    private async Task<SourceFixture> CreateSourceAsync()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var fact = await inputStore.AppendAsync(
            CreateAppend(machineId, $"publication-reader-source-{Guid.NewGuid():N}"),
            CancellationToken.None);
        var aggregationProcessorId = new MetricAggregationProcessorId(
            $"publication-reader-aggregation-{Guid.NewGuid():N}");
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

    private async Task<MetricAggregationCheckpoint> AdvanceSourceAsync(SourceFixture source)
    {
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var fact = await inputStore.AppendAsync(
            CreateAppend(source.MachineId, $"publication-reader-source-{Guid.NewGuid():N}"),
            CancellationToken.None);
        var checkpoint = new MetricAggregationCheckpoint(
            source.Checkpoint.ProcessorId,
            fact.StreamId,
            fact.Position);
        var aggregationStore = new SqlServerMetricAggregationStore(_fixture.ConnectionString);
        await aggregationStore.CommitAsync(
            new MetricAggregationCommit(
                source.Checkpoint.ProcessorId,
                source.Checkpoint,
                checkpoint,
                []),
            CancellationToken.None);
        return checkpoint;
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
        OperationalMetricEvaluationContextKey contextKey) =>
        new(
            machineId,
            periodId,
            new OperationalMetricDefinitionId(metricKey, "1"),
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

    private static OperationalMetricProjection CreateComponentProjection(
        OperationalMetricProjectionProcessorId processorId,
        OperationalMetricEvaluationKey key,
        MetricAggregationCheckpoint revision,
        decimal value,
        string operandName)
    {
        var period = Assert.IsType<OperationalMetricPeriodId.Shift>(key.PeriodId);
        var evidence = new OperationalMetricComponentProjectionEvidence(
            operandName,
            new OperationalMetricAggregateSourceIdentity(
                revision.ProcessorId,
                key.MachineId,
                key.PeriodId,
                "running-duration"),
            revision,
            MetricDimension.Duration,
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
            AssertProjectionSemanticsEqual(
                expectedDependency.Projection,
                actualDependency.Projection);
        }
    }

    private static OperationalMetricProjectionProcessorId NewProcessorId() =>
        new($"publication-reader-{Guid.NewGuid():N}");

    private sealed record SourceFixture(
        MachineId MachineId,
        MetricAggregationCheckpoint Checkpoint);
}
