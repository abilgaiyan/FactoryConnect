using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.Core.Tests;

public sealed class ProductionDayShiftOperationalMetricQueryReaderTests
{
    [Fact]
    public async Task PagesAuthoritativeShiftReportsWithoutDroppingOccurrences()
    {
        var fixture = CreateFixture();
        var reader = new ProductionDayShiftOperationalMetricQueryReader(
            new StubReader(fixture.Reports));

        var first = await reader.ReadAsync(
            fixture.PageQuery(pageSize: 2),
            CancellationToken.None);

        Assert.Equal(2, first.Items.Count);
        Assert.NotNull(first.ContinuationToken);
        Assert.Equal("SHIFT-A", first.Items[0].ShiftOccurrenceId.ShiftId.Value);
        Assert.Equal("SHIFT-B", first.Items[1].ShiftOccurrenceId.ShiftId.Value);

        var second = await reader.ReadAsync(
            fixture.PageQuery(2, first.ContinuationToken),
            CancellationToken.None);

        Assert.Single(second.Items);
        Assert.Equal("SHIFT-C", second.Items[0].ShiftOccurrenceId.ShiftId.Value);
        Assert.Null(second.ContinuationToken);
    }

    [Fact]
    public async Task ContinuationTokenIsBoundToProductionDaySelection()
    {
        var fixture = CreateFixture();
        var reader = new ProductionDayShiftOperationalMetricQueryReader(
            new StubReader(fixture.Reports));
        var first = await reader.ReadAsync(
            fixture.PageQuery(1),
            CancellationToken.None);
        var otherDay = new ProductionDayId(
            fixture.Day.SiteId,
            fixture.Day.BusinessDate.AddDays(1));
        var incompatibleSelection = new ProductionDayShiftOperationalMetricQuery(
            [new ProductionDayShiftReportingSource(fixture.Source, otherDay)],
            OperationalMetricEvaluationContextKey.Unpartitioned);

        await Assert.ThrowsAsync<IncompatibleReportingContinuationTokenException>(async () =>
            await reader.ReadAsync(
                new ProductionDayShiftOperationalMetricPageQuery(
                    incompatibleSelection,
                    new ReportingPageRequest(1, first.ContinuationToken)),
                CancellationToken.None));
    }

    [Fact]
    public async Task MalformedContinuationTokenIsRejected()
    {
        var fixture = CreateFixture();
        var reader = new ProductionDayShiftOperationalMetricQueryReader(
            new StubReader(fixture.Reports));

        await Assert.ThrowsAsync<MalformedReportingContinuationTokenException>(async () =>
            await reader.ReadAsync(
                fixture.PageQuery(
                    1,
                    new ReportingContinuationToken("not-a-valid-token")),
                CancellationToken.None));
    }

    private static Fixture CreateFixture()
    {
        var machineId = new MachineId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var siteId = new SiteId("SITE-A");
        var lineId = new ProductionLineId("LINE-1");
        var day = new ProductionDayId(siteId, new DateOnly(2026, 9, 1));
        var source = new OperationalMetricReportingSource(
            machineId,
            new OperationalMetricProjectionProcessorId("processor"));
        var startsAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var reports = Enumerable.Range(0, 3)
            .Select(index =>
            {
                var shiftName = $"SHIFT-{(char)('A' + index)}";
                var occurrence = new ShiftOccurrenceId(
                    siteId,
                    new ShiftScheduleAssignmentId($"ASSIGN-{index}"),
                    new ShiftId(shiftName),
                    startsAt.AddHours(index * 8),
                    startsAt.AddHours((index + 1) * 8));
                return new ProductionDayShiftOperationalMetricReport(
                    source,
                    day,
                    lineId,
                    occurrence,
                    null,
                    []);
            })
            .ToArray();
        return new Fixture(source, day, reports);
    }

    private sealed record Fixture(
        OperationalMetricReportingSource Source,
        ProductionDayId Day,
        IReadOnlyList<ProductionDayShiftOperationalMetricReport> Reports)
    {
        public ProductionDayShiftOperationalMetricPageQuery PageQuery(
            int pageSize,
            ReportingContinuationToken? continuationToken = null) =>
            new(
                new ProductionDayShiftOperationalMetricQuery(
                    [new ProductionDayShiftReportingSource(Source, Day)],
                    OperationalMetricEvaluationContextKey.Unpartitioned),
                new ReportingPageRequest(pageSize, continuationToken));
    }

    private sealed class StubReader : IProductionDayShiftOperationalMetricReader
    {
        private readonly IReadOnlyList<ProductionDayShiftOperationalMetricReport> _reports;

        public StubReader(IReadOnlyList<ProductionDayShiftOperationalMetricReport> reports) =>
            _reports = reports;

        public ValueTask<IReadOnlyList<ProductionDayShiftOperationalMetricReport>> ReadAsync(
            ProductionDayShiftOperationalMetricQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_reports);
        }
    }
}
