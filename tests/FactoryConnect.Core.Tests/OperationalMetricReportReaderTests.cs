using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.Core.Tests;

public sealed class OperationalMetricReportReaderTests
{
    [Fact]
    public async Task ShiftSummaryReturnsCanonicalMetricsAtOneReportRevision()
    {
        var fixture = CreateFixture();
        var shift = Shift(fixture.SiteId);
        var period = new OperationalMetricPeriodId.Shift(shift);
        await SeedAsync(fixture,
        [
            Projection(fixture, period, BuiltInOperationalMetricDefinitions.QualityId, 0.9m),
            Projection(fixture, period, BuiltInOperationalMetricDefinitions.AvailabilityId, 0.75m),
        ]);

        var report = await fixture.Reader.ReadShiftAsync(
            fixture.ProjectionProcessorId,
            fixture.MachineId,
            shift,
            OperationalMetricEvaluationContextKey.Unpartitioned,
            CancellationToken.None);

        Assert.NotNull(report);
        Assert.Equal(fixture.Revision, report.SourceRevision);
        Assert.Collection(
            report.Metrics,
            metric => Assert.Equal(BuiltInOperationalMetricDefinitions.AvailabilityId, metric.DefinitionId),
            metric => Assert.Equal(BuiltInOperationalMetricDefinitions.QualityId, metric.DefinitionId));
    }

    [Fact]
    public async Task ProductionDaySummaryPreservesBusinessStatusWithoutEvidenceMaterialization()
    {
        var fixture = CreateFixture();
        var day = new ProductionDayId(fixture.SiteId, new DateOnly(2026, 8, 29));
        var period = new OperationalMetricPeriodId.ProductionDay(day);
        var performance = Projection(
            fixture,
            period,
            BuiltInOperationalMetricDefinitions.PerformanceId,
            OperationalMetricEvaluationStatus.InsufficientEvidence,
            null,
            OperationalMetricEvaluationReasonCode.MissingReferenceTime,
            "IdealProductionDuration");
        await SeedAsync(fixture, [performance]);

        var report = await fixture.Reader.ReadProductionDayAsync(
            fixture.ProjectionProcessorId,
            fixture.MachineId,
            day,
            OperationalMetricEvaluationContextKey.Unpartitioned,
            CancellationToken.None);

        Assert.NotNull(report);
        var metric = Assert.Single(report.Metrics);
        Assert.Equal(OperationalMetricEvaluationStatus.InsufficientEvidence, metric.Status);
        Assert.Null(metric.Value);
        Assert.Equal(OperationalMetricEvaluationReasonCode.MissingReferenceTime, metric.ReasonCode);
        Assert.Equal("IdealProductionDuration", metric.ReasonOperandName);
        Assert.Equal(fixture.Revision, report.SourceRevision);
    }

    [Fact]
    public async Task SummaryReadDoesNotLoadDetailEvidence()
    {
        var fixture = CreateFixture();
        var day = new ProductionDayId(fixture.SiteId, new DateOnly(2026, 8, 29));
        var period = new OperationalMetricPeriodId.ProductionDay(day);
        var projection = ProjectionWithComponentEvidence(
            fixture,
            period,
            BuiltInOperationalMetricDefinitions.AvailabilityId,
            0.75m);
        var queryReader = new CountingSplitQueryReader([new OperationalMetricProjectionSummary(projection)], projection);
        var reader = new OperationalMetricReportReader(queryReader);

        var report = await reader.ReadProductionDayAsync(
            fixture.ProjectionProcessorId,
            fixture.MachineId,
            day,
            OperationalMetricEvaluationContextKey.Unpartitioned,
            CancellationToken.None);

        Assert.NotNull(report);
        Assert.Equal(1, queryReader.SummaryReadCount);
        Assert.Equal(0, queryReader.DetailReadCount);
        Assert.DoesNotContain(
            typeof(OperationalMetricReportItem).GetProperties(),
            property => property.Name.Contains("Evidence", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExactVersionDetailLookupPreservesRecursiveLineage()
    {
        var fixture = CreateFixture();
        var day = new ProductionDayId(fixture.SiteId, new DateOnly(2026, 8, 29));
        var period = new OperationalMetricPeriodId.ProductionDay(day);
        var availability = ProjectionWithComponentEvidence(
            fixture,
            period,
            BuiltInOperationalMetricDefinitions.AvailabilityId,
            0.75m);
        var oee = new OperationalMetricProjection(
            fixture.ProjectionProcessorId,
            new OperationalMetricEvaluationKey(
                fixture.MachineId,
                period,
                BuiltInOperationalMetricDefinitions.OeeId,
                OperationalMetricEvaluationContextKey.Unpartitioned),
            OperationalMetricEvaluationStatus.Calculated,
            0.75m,
            OperationalMetricUnits.Ratio,
            null,
            null,
            fixture.Revision,
            dependencyEvidence:
            [
                new OperationalMetricDependencyProjectionEvidence(
                    "Availability",
                    BuiltInOperationalMetricDefinitions.AvailabilityId,
                    availability),
            ]);
        await SeedAsync(fixture, [availability, oee]);

        var detail = await fixture.Reader.ReadMetricDetailAsync(
            fixture.ProjectionProcessorId,
            fixture.MachineId,
            period,
            OperationalMetricEvaluationContextKey.Unpartitioned,
            BuiltInOperationalMetricDefinitions.OeeId,
            CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(BuiltInOperationalMetricDefinitions.OeeId, detail.Key.DefinitionId);
        var dependency = Assert.Single(detail.DependencyEvidence);
        Assert.Equal(BuiltInOperationalMetricDefinitions.AvailabilityId, dependency.DefinitionId);
        var component = Assert.Single(dependency.Projection.OperandEvidence);
        Assert.Equal(MetricInputKeys.ActualProductionTime, component.SourceIdentity.ComponentKey);
        Assert.Equal(fixture.Revision, component.SourceRevision);
    }

    [Fact]
    public async Task DetailLookupRequiresExactDefinitionVersion()
    {
        var fixture = CreateFixture();
        var day = new ProductionDayId(fixture.SiteId, new DateOnly(2026, 8, 29));
        var period = new OperationalMetricPeriodId.ProductionDay(day);
        await SeedAsync(fixture,
        [
            Projection(fixture, period, BuiltInOperationalMetricDefinitions.AvailabilityId, 0.75m),
        ]);

        var missing = await fixture.Reader.ReadMetricDetailAsync(
            fixture.ProjectionProcessorId,
            fixture.MachineId,
            period,
            OperationalMetricEvaluationContextKey.Unpartitioned,
            new OperationalMetricDefinitionId(CanonicalMetricKeys.Availability, "2.0"),
            CancellationToken.None);

        Assert.Null(missing);
    }

    [Fact]
    public async Task MixedSourceRevisionsFailPeriodSummary()
    {
        var fixture = CreateFixture();
        var day = new ProductionDayId(fixture.SiteId, new DateOnly(2026, 8, 29));
        var period = new OperationalMetricPeriodId.ProductionDay(day);
        var availability = Projection(fixture, period, BuiltInOperationalMetricDefinitions.AvailabilityId, 0.75m);
        var laterRevision = new MetricAggregationCheckpoint(
            fixture.Revision.ProcessorId,
            fixture.Revision.StreamId,
            new MetricInputPosition(fixture.Revision.Position.Value + 1));
        var quality = Projection(
            fixture,
            period,
            BuiltInOperationalMetricDefinitions.QualityId,
            OperationalMetricEvaluationStatus.Calculated,
            0.9m,
            null,
            null,
            laterRevision);
        var reader = new OperationalMetricReportReader(
            new FixedSummaryReader(
            [
                new OperationalMetricProjectionSummary(availability),
                new OperationalMetricProjectionSummary(quality),
            ]));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await reader.ReadProductionDayAsync(
                fixture.ProjectionProcessorId,
                fixture.MachineId,
                day,
                OperationalMetricEvaluationContextKey.Unpartitioned,
                CancellationToken.None));
    }

    [Fact]
    public async Task MissingPeriodReturnsNullReport()
    {
        var fixture = CreateFixture();

        Assert.Null(await fixture.Reader.ReadProductionDayAsync(
            fixture.ProjectionProcessorId,
            fixture.MachineId,
            new ProductionDayId(fixture.SiteId, new DateOnly(2026, 8, 29)),
            OperationalMetricEvaluationContextKey.Unpartitioned,
            CancellationToken.None));
    }

    [Fact]
    public async Task ReportingQueryIsIsolatedByMachinePeriodContextAndProcessor()
    {
        var fixture = CreateFixture();
        var day = new ProductionDayId(fixture.SiteId, new DateOnly(2026, 8, 29));
        var period = new OperationalMetricPeriodId.ProductionDay(day);
        await SeedAsync(fixture,
        [
            Projection(fixture, period, BuiltInOperationalMetricDefinitions.AvailabilityId, 0.75m),
        ]);

        Assert.Null(await fixture.Reader.ReadProductionDayAsync(
            fixture.ProjectionProcessorId,
            MachineId.New(),
            day,
            OperationalMetricEvaluationContextKey.Unpartitioned,
            CancellationToken.None));
        Assert.Null(await fixture.Reader.ReadProductionDayAsync(
            new OperationalMetricProjectionProcessorId("projection-other"),
            fixture.MachineId,
            day,
            OperationalMetricEvaluationContextKey.Unpartitioned,
            CancellationToken.None));
        Assert.Null(await fixture.Reader.ReadProductionDayAsync(
            fixture.ProjectionProcessorId,
            fixture.MachineId,
            day,
            new OperationalMetricEvaluationContextKey
            {
                ProductionOrderId = new ProductionOrderId("order-1"),
            },
            CancellationToken.None));
    }

    [Fact]
    public async Task SummaryReaderReturningWrongIdentityFailsReportingRead()
    {
        var fixture = CreateFixture();
        var requestedDay = new ProductionDayId(fixture.SiteId, new DateOnly(2026, 8, 29));
        var wrongDay = new ProductionDayId(fixture.SiteId, new DateOnly(2026, 8, 30));
        var wrongProjection = Projection(
            fixture,
            new OperationalMetricPeriodId.ProductionDay(wrongDay),
            BuiltInOperationalMetricDefinitions.AvailabilityId,
            0.5m);
        var reader = new OperationalMetricReportReader(
            new FixedSummaryReader([new OperationalMetricProjectionSummary(wrongProjection)]));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await reader.ReadProductionDayAsync(
                fixture.ProjectionProcessorId,
                fixture.MachineId,
                requestedDay,
                OperationalMetricEvaluationContextKey.Unpartitioned,
                CancellationToken.None));
    }

    [Fact]
    public async Task PreCancelledSummaryReadDoesNotQueryProjectionStore()
    {
        var fixture = CreateFixture();
        var queryReader = new CountingSplitQueryReader([], null);
        var reader = new OperationalMetricReportReader(queryReader);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await reader.ReadProductionDayAsync(
                fixture.ProjectionProcessorId,
                fixture.MachineId,
                new ProductionDayId(fixture.SiteId, new DateOnly(2026, 8, 29)),
                OperationalMetricEvaluationContextKey.Unpartitioned,
                cancellation.Token));

        Assert.Equal(0, queryReader.SummaryReadCount);
        Assert.Equal(0, queryReader.DetailReadCount);
    }

    private static ReportFixture CreateFixture()
    {
        var machineId = MachineId.New();
        var siteId = new SiteId("site-a");
        var aggregationProcessorId = new MetricAggregationProcessorId("aggregate-m01");
        var streamId = MetricInputStreamId.ForMachine(machineId);
        var revision = new MetricAggregationCheckpoint(
            aggregationProcessorId,
            streamId,
            new MetricInputPosition(10));
        var projectionProcessorId = new OperationalMetricProjectionProcessorId("projection-m01");
        var store = new InMemoryOperationalMetricProjectionStore();
        return new ReportFixture(
            machineId,
            siteId,
            revision,
            projectionProcessorId,
            store,
            new OperationalMetricReportReader(store));
    }

    private static ShiftOccurrenceId Shift(SiteId siteId) => new(
        siteId,
        new ShiftScheduleAssignmentId("schedule-a"),
        new ShiftId("shift-a"),
        new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 8, 29, 8, 0, 0, TimeSpan.Zero));

    private static OperationalMetricProjection Projection(
        ReportFixture fixture,
        OperationalMetricPeriodId periodId,
        OperationalMetricDefinitionId definitionId,
        decimal value) =>
        Projection(
            fixture,
            periodId,
            definitionId,
            OperationalMetricEvaluationStatus.Calculated,
            value,
            null,
            null);

    private static OperationalMetricProjection Projection(
        ReportFixture fixture,
        OperationalMetricPeriodId periodId,
        OperationalMetricDefinitionId definitionId,
        OperationalMetricEvaluationStatus status,
        decimal? value,
        OperationalMetricEvaluationReasonCode? reasonCode,
        string? reasonOperandName,
        MetricAggregationCheckpoint? sourceRevision = null) => new(
            fixture.ProjectionProcessorId,
            new OperationalMetricEvaluationKey(
                fixture.MachineId,
                periodId,
                definitionId,
                OperationalMetricEvaluationContextKey.Unpartitioned),
            status,
            value,
            OperationalMetricUnits.Ratio,
            reasonCode,
            reasonOperandName,
            sourceRevision ?? fixture.Revision);

    private static OperationalMetricProjection ProjectionWithComponentEvidence(
        ReportFixture fixture,
        OperationalMetricPeriodId periodId,
        OperationalMetricDefinitionId definitionId,
        decimal value)
    {
        var sourceIdentity = new OperationalMetricAggregateSourceIdentity(
            fixture.Revision.ProcessorId,
            fixture.MachineId,
            periodId,
            MetricInputKeys.ActualProductionTime);
        var evidence = new OperationalMetricComponentProjectionEvidence(
            "ActualProductionTime",
            sourceIdentity,
            fixture.Revision,
            MetricDimension.Duration,
            300m,
            MetricInputFactUnits.Seconds,
            1,
            new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 29, 1, 0, 0, TimeSpan.Zero));
        return new OperationalMetricProjection(
            fixture.ProjectionProcessorId,
            new OperationalMetricEvaluationKey(
                fixture.MachineId,
                periodId,
                definitionId,
                OperationalMetricEvaluationContextKey.Unpartitioned),
            OperationalMetricEvaluationStatus.Calculated,
            value,
            OperationalMetricUnits.Ratio,
            null,
            null,
            fixture.Revision,
            [evidence]);
    }

    private static ValueTask SeedAsync(
        ReportFixture fixture,
        IReadOnlyList<OperationalMetricProjection> projections)
    {
        var manifest = new OperationalMetricProjectionBatchManifest(
            projections.Select(static projection => projection.Key));
        var checkpoint = new OperationalMetricProjectionCheckpoint(
            fixture.ProjectionProcessorId,
            fixture.Revision,
            manifest);
        return fixture.Store.CommitAsync(
            new OperationalMetricProjectionCommit(
                fixture.ProjectionProcessorId,
                null,
                checkpoint,
                projections),
            CancellationToken.None);
    }

    private sealed class FixedSummaryReader : IOperationalMetricProjectionQueryReader
    {
        private readonly IReadOnlyList<OperationalMetricProjectionSummary> _summaries;

        public FixedSummaryReader(IReadOnlyList<OperationalMetricProjectionSummary> summaries)
        {
            _summaries = summaries;
        }

        public ValueTask<IReadOnlyList<OperationalMetricProjectionSummary>> ReadPeriodSummariesAsync(
            OperationalMetricProjectionProcessorId processorId,
            MachineId machineId,
            OperationalMetricPeriodId periodId,
            OperationalMetricEvaluationContextKey contextKey,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(_summaries);

        public ValueTask<OperationalMetricProjection?> ReadDetailAsync(
            OperationalMetricProjectionProcessorId processorId,
            OperationalMetricEvaluationKey key,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<OperationalMetricProjection?>(null);
    }

    private sealed class CountingSplitQueryReader : IOperationalMetricProjectionQueryReader
    {
        private readonly IReadOnlyList<OperationalMetricProjectionSummary> _summaries;
        private readonly OperationalMetricProjection? _detail;

        public CountingSplitQueryReader(
            IReadOnlyList<OperationalMetricProjectionSummary> summaries,
            OperationalMetricProjection? detail)
        {
            _summaries = summaries;
            _detail = detail;
        }

        public int SummaryReadCount { get; private set; }

        public int DetailReadCount { get; private set; }

        public ValueTask<IReadOnlyList<OperationalMetricProjectionSummary>> ReadPeriodSummariesAsync(
            OperationalMetricProjectionProcessorId processorId,
            MachineId machineId,
            OperationalMetricPeriodId periodId,
            OperationalMetricEvaluationContextKey contextKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SummaryReadCount++;
            return ValueTask.FromResult(_summaries);
        }

        public ValueTask<OperationalMetricProjection?> ReadDetailAsync(
            OperationalMetricProjectionProcessorId processorId,
            OperationalMetricEvaluationKey key,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DetailReadCount++;
            return ValueTask.FromResult(_detail);
        }
    }

    private sealed record ReportFixture(
        MachineId MachineId,
        SiteId SiteId,
        MetricAggregationCheckpoint Revision,
        OperationalMetricProjectionProcessorId ProjectionProcessorId,
        InMemoryOperationalMetricProjectionStore Store,
        OperationalMetricReportReader Reader);
}
