using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.Core.Tests;

public sealed class OperationalMetricReportReaderTests
{
    [Fact]
    public async Task ShiftReportReturnsDurableMetricsInCanonicalDefinitionOrder()
    {
        var fixture = CreateFixture();
        var shift = Shift(fixture.SiteId);
        var period = new OperationalMetricPeriodId.Shift(shift);
        var quality = Projection(
            fixture,
            period,
            BuiltInOperationalMetricDefinitions.QualityId,
            OperationalMetricEvaluationStatus.Calculated,
            0.9m,
            null,
            null);
        var availability = Projection(
            fixture,
            period,
            BuiltInOperationalMetricDefinitions.AvailabilityId,
            OperationalMetricEvaluationStatus.Calculated,
            0.75m,
            null,
            null);
        await SeedAsync(fixture, [quality, availability]);

        var report = await fixture.Reader.ReadShiftAsync(
            fixture.ProjectionProcessorId,
            fixture.MachineId,
            shift,
            OperationalMetricEvaluationContextKey.Unpartitioned,
            CancellationToken.None);

        Assert.NotNull(report);
        Assert.Equal(fixture.MachineId, report.MachineId);
        Assert.Equal(shift, report.ShiftOccurrenceId);
        Assert.Collection(
            report.Metrics,
            metric => Assert.Equal(BuiltInOperationalMetricDefinitions.AvailabilityId, metric.DefinitionId),
            metric => Assert.Equal(BuiltInOperationalMetricDefinitions.QualityId, metric.DefinitionId));
        Assert.All(report.Metrics, metric => Assert.Equal(fixture.Revision, metric.SourceRevision));
    }

    [Fact]
    public async Task ProductionDayReportPreservesBusinessStatusAndReasonWithoutRecalculation()
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
        Assert.Equal(OperationalMetricUnits.Ratio, metric.Unit);
    }

    [Fact]
    public async Task MissingPeriodReturnsNullReport()
    {
        var fixture = CreateFixture();
        var day = new ProductionDayId(fixture.SiteId, new DateOnly(2026, 8, 29));

        var report = await fixture.Reader.ReadProductionDayAsync(
            fixture.ProjectionProcessorId,
            fixture.MachineId,
            day,
            OperationalMetricEvaluationContextKey.Unpartitioned,
            CancellationToken.None);

        Assert.Null(report);
    }

    [Fact]
    public async Task ReportingQueryIsIsolatedByMachinePeriodContextAndProcessor()
    {
        var fixture = CreateFixture();
        var day = new ProductionDayId(fixture.SiteId, new DateOnly(2026, 8, 29));
        var period = new OperationalMetricPeriodId.ProductionDay(day);
        await SeedAsync(fixture,
        [
            Projection(
                fixture,
                period,
                BuiltInOperationalMetricDefinitions.AvailabilityId,
                OperationalMetricEvaluationStatus.Calculated,
                0.75m,
                null,
                null),
        ]);

        var otherMachine = MachineId.New();
        var otherProcessor = new OperationalMetricProjectionProcessorId("projection-other");
        var otherContext = new OperationalMetricEvaluationContextKey
        {
            ProductionOrderId = new ProductionOrderId("order-1"),
        };

        Assert.Null(await fixture.Reader.ReadProductionDayAsync(
            fixture.ProjectionProcessorId,
            otherMachine,
            day,
            OperationalMetricEvaluationContextKey.Unpartitioned,
            CancellationToken.None));
        Assert.Null(await fixture.Reader.ReadProductionDayAsync(
            otherProcessor,
            fixture.MachineId,
            day,
            OperationalMetricEvaluationContextKey.Unpartitioned,
            CancellationToken.None));
        Assert.Null(await fixture.Reader.ReadProductionDayAsync(
            fixture.ProjectionProcessorId,
            fixture.MachineId,
            day,
            otherContext,
            CancellationToken.None));
    }

    [Fact]
    public async Task ProjectionReaderReturningWrongIdentityFailsReportingRead()
    {
        var fixture = CreateFixture();
        var day = new ProductionDayId(fixture.SiteId, new DateOnly(2026, 8, 29));
        var wrongDay = new ProductionDayId(fixture.SiteId, new DateOnly(2026, 8, 30));
        var wrongProjection = Projection(
            fixture,
            new OperationalMetricPeriodId.ProductionDay(wrongDay),
            BuiltInOperationalMetricDefinitions.AvailabilityId,
            OperationalMetricEvaluationStatus.Calculated,
            0.5m,
            null,
            null);
        var reader = new OperationalMetricReportReader(new FixedProjectionReader([wrongProjection]));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await reader.ReadProductionDayAsync(
                fixture.ProjectionProcessorId,
                fixture.MachineId,
                day,
                OperationalMetricEvaluationContextKey.Unpartitioned,
                CancellationToken.None));
    }

    [Fact]
    public async Task PreCancelledReadDoesNotQueryProjectionStore()
    {
        var fixture = CreateFixture();
        var queryReader = new CountingProjectionReader();
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

        Assert.Equal(0, queryReader.ReadCount);
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
        OperationalMetricEvaluationStatus status,
        decimal? value,
        OperationalMetricEvaluationReasonCode? reasonCode,
        string? reasonOperandName) => new(
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
            fixture.Revision);

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

    private sealed class FixedProjectionReader : IOperationalMetricProjectionQueryReader
    {
        private readonly IReadOnlyList<OperationalMetricProjection> _projections;

        public FixedProjectionReader(IReadOnlyList<OperationalMetricProjection> projections)
        {
            _projections = projections;
        }

        public ValueTask<IReadOnlyList<OperationalMetricProjection>> ReadPeriodAsync(
            OperationalMetricProjectionProcessorId processorId,
            MachineId machineId,
            OperationalMetricPeriodId periodId,
            OperationalMetricEvaluationContextKey contextKey,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(_projections);
    }

    private sealed class CountingProjectionReader : IOperationalMetricProjectionQueryReader
    {
        public int ReadCount { get; private set; }

        public ValueTask<IReadOnlyList<OperationalMetricProjection>> ReadPeriodAsync(
            OperationalMetricProjectionProcessorId processorId,
            MachineId machineId,
            OperationalMetricPeriodId periodId,
            OperationalMetricEvaluationContextKey contextKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return ValueTask.FromResult<IReadOnlyList<OperationalMetricProjection>>([]);
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
