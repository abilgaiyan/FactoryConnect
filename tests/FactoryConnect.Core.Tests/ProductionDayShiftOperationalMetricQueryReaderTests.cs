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
    public async Task FullMultiPageTraversalReturnsEveryOccurrenceExactlyOnce()
    {
        var fixture = CreateFixture();
        var reader = new ProductionDayShiftOperationalMetricQueryReader(
            new StubReader(fixture.Reports.Reverse().ToArray()));
        var seen = new List<ShiftOccurrenceId>();
        ReportingContinuationToken? token = null;

        do
        {
            var page = await reader.ReadAsync(
                fixture.PageQuery(1, token),
                CancellationToken.None);
            seen.AddRange(page.Items.Select(static item => item.ShiftOccurrenceId));
            token = page.ContinuationToken;
        }
        while (token is not null);

        Assert.Equal(3, seen.Count);
        Assert.Equal(3, seen.Distinct().Count());
        Assert.Equal(
            ["SHIFT-A", "SHIFT-B", "SHIFT-C"],
            seen.Select(static occurrence => occurrence.ShiftId.Value).ToArray());
    }

    [Fact]
    public async Task NonDefaultContextSurvivesPagedOrchestrationExactly()
    {
        var fixture = CreateFixture();
        var reader = new ProductionDayShiftOperationalMetricQueryReader(
            new StubReader(fixture.Reports));

        var first = await reader.ReadAsync(fixture.PageQuery(1), CancellationToken.None);
        var firstReport = Assert.Single(first.Items);
        Assert.Same(fixture.Context, firstReport.ContextKey);

        var second = await reader.ReadAsync(
            fixture.PageQuery(1, first.ContinuationToken),
            CancellationToken.None);
        var secondReport = Assert.Single(second.Items);
        Assert.Same(fixture.Context, secondReport.ContextKey);
    }

    [Fact]
    public async Task UnexpectedSourceOutputIsRejectedBeforePagination()
    {
        var fixture = CreateFixture();
        var otherSource = new OperationalMetricReportingSource(
            MachineId.New(),
            new OperationalMetricProjectionProcessorId("other-processor"));
        var unexpected = new ProductionDayShiftOperationalMetricReport(
            otherSource,
            fixture.Day,
            new ProductionLineId("LINE-1"),
            fixture.Reports[0].ShiftOccurrenceId,
            fixture.Context,
            null,
            []);
        var reader = new ProductionDayShiftOperationalMetricQueryReader(
            new StubReader([unexpected]));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await reader.ReadAsync(fixture.PageQuery(1), CancellationToken.None));
    }

    [Fact]
    public async Task DuplicateAuthoritativeIdentityIsRejectedBeforePagination()
    {
        var fixture = CreateFixture();
        var duplicate = fixture.Reports[0];
        var reader = new ProductionDayShiftOperationalMetricQueryReader(
            new StubReader([duplicate, duplicate]));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await reader.ReadAsync(fixture.PageQuery(1), CancellationToken.None));
    }

    [Fact]
    public async Task ReportOutsideRequestedContextIsRejectedBeforePagination()
    {
        var fixture = CreateFixture();
        var wrongContext = OperationalMetricEvaluationContextKey.Unpartitioned;
        var unexpected = new ProductionDayShiftOperationalMetricReport(
            fixture.Source,
            fixture.Day,
            new ProductionLineId("LINE-1"),
            fixture.Reports[0].ShiftOccurrenceId,
            wrongContext,
            null,
            []);
        var reader = new ProductionDayShiftOperationalMetricQueryReader(
            new StubReader([unexpected]));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await reader.ReadAsync(fixture.PageQuery(1), CancellationToken.None));
    }

    [Fact]
    public async Task ContinuationTokenIsBoundToProductionDaySelectionBeforeAuthoritativeRead()
    {
        var fixture = CreateFixture();
        var initialStub = new StubReader(fixture.Reports);
        var initialReader = new ProductionDayShiftOperationalMetricQueryReader(initialStub);
        var first = await initialReader.ReadAsync(
            fixture.PageQuery(1),
            CancellationToken.None);
        var otherDay = new ProductionDayId(
            fixture.Day.SiteId,
            fixture.Day.BusinessDate.AddDays(1));
        var incompatibleSelection = new ProductionDayShiftOperationalMetricQuery(
            [new ProductionDayShiftReportingSource(fixture.Source, otherDay)],
            fixture.Context);
        var rejectingStub = new StubReader(fixture.Reports);
        var rejectingReader = new ProductionDayShiftOperationalMetricQueryReader(rejectingStub);

        await Assert.ThrowsAsync<IncompatibleReportingContinuationTokenException>(async () =>
            await rejectingReader.ReadAsync(
                new ProductionDayShiftOperationalMetricPageQuery(
                    incompatibleSelection,
                    new ReportingPageRequest(1, first.ContinuationToken)),
                CancellationToken.None));

        Assert.Equal(0, rejectingStub.ReadCount);
    }

    [Fact]
    public async Task MalformedContinuationTokenIsRejectedBeforeAuthoritativeRead()
    {
        var fixture = CreateFixture();
        var stub = new StubReader(fixture.Reports);
        var reader = new ProductionDayShiftOperationalMetricQueryReader(stub);

        await Assert.ThrowsAsync<MalformedReportingContinuationTokenException>(async () =>
            await reader.ReadAsync(
                fixture.PageQuery(
                    1,
                    new ReportingContinuationToken("not-a-valid-token")),
                CancellationToken.None));

        Assert.Equal(0, stub.ReadCount);
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
        var context = new OperationalMetricEvaluationContextKey
        {
            ProductionOrderId = new ProductionOrderId("ORDER-42"),
            OperationId = new OperationId("OP-10"),
        };
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
                    context,
                    null,
                    []);
            })
            .ToArray();
        return new Fixture(source, day, context, reports);
    }

    private sealed record Fixture(
        OperationalMetricReportingSource Source,
        ProductionDayId Day,
        OperationalMetricEvaluationContextKey Context,
        IReadOnlyList<ProductionDayShiftOperationalMetricReport> Reports)
    {
        public ProductionDayShiftOperationalMetricPageQuery PageQuery(
            int pageSize,
            ReportingContinuationToken? continuationToken = null) =>
            new(
                new ProductionDayShiftOperationalMetricQuery(
                    [new ProductionDayShiftReportingSource(Source, Day)],
                    Context),
                new ReportingPageRequest(pageSize, continuationToken));
    }

    private sealed class StubReader : IProductionDayShiftOperationalMetricReader
    {
        private readonly IReadOnlyList<ProductionDayShiftOperationalMetricReport> _reports;

        public StubReader(IReadOnlyList<ProductionDayShiftOperationalMetricReport> reports) =>
            _reports = reports;

        public int ReadCount { get; private set; }

        public ValueTask<IReadOnlyList<ProductionDayShiftOperationalMetricReport>> ReadAsync(
            ProductionDayShiftOperationalMetricQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return ValueTask.FromResult(_reports);
        }
    }
}
