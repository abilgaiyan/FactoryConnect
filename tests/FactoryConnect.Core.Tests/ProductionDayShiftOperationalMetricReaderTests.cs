using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.Core.Tests;

public sealed class ProductionDayShiftOperationalMetricReaderTests
{
    [Fact]
    public async Task MissingRosterCoverageFailsInsteadOfReportingEmptyDay()
    {
        var fixture = CreateFixture();

        var exception = await Assert.ThrowsAsync<ProductionDayShiftRosterCoverageRequiredException>(
            async () => await fixture.Reader.ReadAsync(fixture.Query(), CancellationToken.None));

        Assert.Equal(fixture.MachineId, exception.MachineId);
        Assert.Equal(fixture.Day, exception.ProductionDayId);
        Assert.Equal(0, fixture.MetricReader.ShiftReadCount);
    }

    [Fact]
    public async Task CoveredEmptyRosterReturnsAuthoritativeEmptyResult()
    {
        var fixture = CreateFixture();
        await fixture.PublishRosterAsync([]);

        var reports = await fixture.Reader.ReadAsync(fixture.Query(), CancellationToken.None);

        Assert.Empty(reports);
        Assert.Equal(0, fixture.MetricReader.ShiftReadCount);
    }

    [Fact]
    public async Task RosterOccurrencesExistWithoutMetricEvidence()
    {
        var fixture = CreateFixture();
        var second = new ShiftOccurrenceId(
            fixture.SiteId,
            new ShiftScheduleAssignmentId("ASSIGN-B"),
            new ShiftId("SHIFT-B"),
            fixture.Occurrence.EndsAtUtc,
            fixture.Occurrence.EndsAtUtc.AddHours(8));
        await fixture.PublishRosterAsync([second, fixture.Occurrence]);

        var reports = await fixture.Reader.ReadAsync(fixture.Query(), CancellationToken.None);

        Assert.Equal(2, reports.Count);
        Assert.Equal(fixture.Occurrence, reports[0].ShiftOccurrenceId);
        Assert.Equal(second, reports[1].ShiftOccurrenceId);
        Assert.All(reports, report =>
        {
            Assert.Equal(fixture.MachineId, report.Source.MachineId);
            Assert.Equal(fixture.Day, report.ProductionDayId);
            Assert.Equal(fixture.LineId, report.ProductionLineId);
            Assert.Same(OperationalMetricEvaluationContextKey.Unpartitioned, report.ContextKey);
            Assert.Null(report.SourceRevision);
            Assert.Empty(report.Metrics);
        });
        Assert.Equal(2, fixture.MetricReader.ShiftReadCount);
    }

    [Fact]
    public async Task NonDefaultContextIsPreservedExactly()
    {
        var fixture = CreateFixture();
        await fixture.PublishRosterAsync([fixture.Occurrence]);
        var context = new OperationalMetricEvaluationContextKey
        {
            ProductionOrderId = new ProductionOrderId("ORDER-42"),
            OperationId = new OperationId("OP-10"),
        };
        var revision = fixture.Revision();
        fixture.MetricReader.ShiftReport = fixture.MetricReport(
            revision,
            BuiltInOperationalMetricDefinitions.AvailabilityId,
            contextKey: context);

        var report = Assert.Single(
            await fixture.Reader.ReadAsync(fixture.Query(contextKey: context), CancellationToken.None));

        Assert.Same(context, report.ContextKey);
    }

    [Fact]
    public async Task AuthoritativeSourceRevisionIsPreservedExactly()
    {
        var fixture = CreateFixture();
        await fixture.PublishRosterAsync([fixture.Occurrence]);
        var revision = fixture.Revision();
        fixture.MetricReader.ShiftReport = fixture.MetricReport(
            revision,
            BuiltInOperationalMetricDefinitions.AvailabilityId);

        var report = Assert.Single(
            await fixture.Reader.ReadAsync(fixture.Query(), CancellationToken.None));

        Assert.Same(revision, report.SourceRevision);
        Assert.Equal(
            BuiltInOperationalMetricDefinitions.AvailabilityId,
            Assert.Single(report.Metrics).DefinitionId);
    }

    [Fact]
    public async Task SourceRevisionRemainsWhenMetricFilterEliminatesEveryMetric()
    {
        var fixture = CreateFixture();
        await fixture.PublishRosterAsync([fixture.Occurrence]);
        var revision = fixture.Revision();
        fixture.MetricReader.ShiftReport = fixture.MetricReport(
            revision,
            BuiltInOperationalMetricDefinitions.AvailabilityId);
        var metrics = new OperationalMetricDefinitionSelection(
            [BuiltInOperationalMetricDefinitions.OeeId]);

        var report = Assert.Single(
            await fixture.Reader.ReadAsync(fixture.Query(metrics), CancellationToken.None));

        Assert.Same(revision, report.SourceRevision);
        Assert.Empty(report.Metrics);
    }

    [Fact]
    public async Task MismatchedMetricReportIsRejectedBeforeRevisionOrMetricsAreExposed()
    {
        var fixture = CreateFixture();
        await fixture.PublishRosterAsync([fixture.Occurrence]);
        var otherMachineId = MachineId.New();
        var revision = fixture.Revision(otherMachineId);
        fixture.MetricReader.ShiftReport = fixture.MetricReport(
            revision,
            BuiltInOperationalMetricDefinitions.AvailabilityId,
            otherMachineId);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.Reader.ReadAsync(fixture.Query(), CancellationToken.None));
    }

    [Fact]
    public async Task OvernightOccurrenceRemainsOwnedByRosterProductionDay()
    {
        var fixture = CreateFixture();
        var overnight = new ShiftOccurrenceId(
            fixture.SiteId,
            new ShiftScheduleAssignmentId("ASSIGN-NIGHT"),
            new ShiftId("NIGHT"),
            new DateTimeOffset(2026, 9, 1, 20, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 2, 4, 0, 0, TimeSpan.Zero));
        await fixture.PublishRosterAsync([overnight]);

        var report = Assert.Single(
            await fixture.Reader.ReadAsync(fixture.Query(), CancellationToken.None));

        Assert.Equal(fixture.Day, report.ProductionDayId);
        Assert.Equal(overnight, report.ShiftOccurrenceId);
    }

    private static Fixture CreateFixture()
    {
        var machineId = new MachineId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var siteId = new SiteId("SITE-A");
        var lineId = new ProductionLineId("LINE-1");
        var day = new ProductionDayId(siteId, new DateOnly(2026, 9, 1));
        var occurrence = new ShiftOccurrenceId(
            siteId,
            new ShiftScheduleAssignmentId("ASSIGN-A"),
            new ShiftId("SHIFT-A"),
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero));
        var source = new OperationalMetricReportingSource(
            machineId,
            new OperationalMetricProjectionProcessorId("processor"));
        var rosterStore = new InMemoryMachineShiftOccurrenceRosterStore();
        var metricReader = new StubMetricReader();
        var reader = new ProductionDayShiftOperationalMetricReader(rosterStore, metricReader);
        return new Fixture(machineId, siteId, lineId, day, occurrence, source, rosterStore, metricReader, reader);
    }

    private sealed record Fixture(
        MachineId MachineId,
        SiteId SiteId,
        ProductionLineId LineId,
        ProductionDayId Day,
        ShiftOccurrenceId Occurrence,
        OperationalMetricReportingSource Source,
        InMemoryMachineShiftOccurrenceRosterStore RosterStore,
        StubMetricReader MetricReader,
        ProductionDayShiftOperationalMetricReader Reader)
    {
        public ProductionDayShiftOperationalMetricQuery Query(
            OperationalMetricDefinitionSelection? metrics = null,
            OperationalMetricEvaluationContextKey? contextKey = null) =>
            new(
                [new ProductionDayShiftReportingSource(Source, Day)],
                contextKey ?? OperationalMetricEvaluationContextKey.Unpartitioned,
                metrics);

        public MetricAggregationCheckpoint Revision(MachineId? machineId = null) =>
            new(
                new MetricAggregationProcessorId("aggregate"),
                MetricInputStreamId.ForMachine(machineId ?? MachineId),
                new MetricInputPosition(12));

        public ShiftOperationalMetricReport MetricReport(
            MetricAggregationCheckpoint revision,
            OperationalMetricDefinitionId definitionId,
            MachineId? machineId = null,
            OperationalMetricEvaluationContextKey? contextKey = null)
        {
            var reportMachineId = machineId ?? MachineId;
            var reportContext = contextKey ?? OperationalMetricEvaluationContextKey.Unpartitioned;
            var summary = new OperationalMetricProjectionSummary(
                Source.ProcessorId,
                new OperationalMetricEvaluationKey(
                    reportMachineId,
                    new OperationalMetricPeriodId.Shift(Occurrence),
                    definitionId,
                    reportContext),
                OperationalMetricEvaluationStatus.Calculated,
                0.8m,
                OperationalMetricUnits.Ratio,
                null,
                null,
                revision);
            return new ShiftOperationalMetricReport(
                Source.ProcessorId,
                reportMachineId,
                Occurrence,
                reportContext,
                revision,
                [new OperationalMetricReportItem(summary)]);
        }

        public ValueTask PublishRosterAsync(IReadOnlyList<ShiftOccurrenceId> occurrences)
        {
            var roster = new MachineShiftOccurrenceRoster(
                MachineId,
                LineId,
                Day,
                new MachineShiftOccurrenceRosterRevision(1),
                occurrences.Select(occurrence => new MachineShiftOccurrenceOwnership(
                    MachineId,
                    LineId,
                    occurrence,
                    Day)));
            return RosterStore.CommitAsync(
                new MachineShiftOccurrenceRosterCommit(null, roster),
                CancellationToken.None);
        }
    }

    private sealed class StubMetricReader : IOperationalMetricReportReader
    {
        public int ShiftReadCount { get; private set; }

        public ShiftOperationalMetricReport? ShiftReport { get; set; }

        public ValueTask<ShiftOperationalMetricReport?> ReadShiftAsync(
            OperationalMetricProjectionProcessorId processorId,
            MachineId machineId,
            ShiftOccurrenceId shiftOccurrenceId,
            OperationalMetricEvaluationContextKey contextKey,
            CancellationToken cancellationToken)
        {
            ShiftReadCount++;
            return ValueTask.FromResult(ShiftReport);
        }

        public ValueTask<ProductionDayOperationalMetricReport?> ReadProductionDayAsync(
            OperationalMetricProjectionProcessorId processorId,
            MachineId machineId,
            ProductionDayId productionDayId,
            OperationalMetricEvaluationContextKey contextKey,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<ProductionDayOperationalMetricReport?>(null);

        public ValueTask<OperationalMetricReportDetail?> ReadMetricDetailAsync(
            OperationalMetricProjectionProcessorId processorId,
            MachineId machineId,
            OperationalMetricPeriodId periodId,
            OperationalMetricEvaluationContextKey contextKey,
            OperationalMetricDefinitionId definitionId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<OperationalMetricReportDetail?>(null);
    }
}
