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
            Assert.Empty(report.Metrics);
        });
        Assert.Equal(2, fixture.MetricReader.ShiftReadCount);
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
        var metricReader = new EmptyMetricReader();
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
        EmptyMetricReader MetricReader,
        ProductionDayShiftOperationalMetricReader Reader)
    {
        public ProductionDayShiftOperationalMetricQuery Query() =>
            new(
                [new ProductionDayShiftReportingSource(Source, Day)],
                OperationalMetricEvaluationContextKey.Unpartitioned);

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

    private sealed class EmptyMetricReader : IOperationalMetricReportReader
    {
        public int ShiftReadCount { get; private set; }

        public ValueTask<ShiftOperationalMetricReport?> ReadShiftAsync(
            OperationalMetricProjectionProcessorId processorId,
            MachineId machineId,
            ShiftOccurrenceId shiftOccurrenceId,
            OperationalMetricEvaluationContextKey contextKey,
            CancellationToken cancellationToken)
        {
            ShiftReadCount++;
            return ValueTask.FromResult<ShiftOperationalMetricReport?>(null);
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
