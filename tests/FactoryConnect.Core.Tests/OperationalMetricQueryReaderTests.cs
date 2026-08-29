using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.Core.Tests;

public sealed class OperationalMetricQueryReaderTests
{
    [Fact]
    public async Task MapsProductionDaySummaryWithoutChangingContinuationToken()
    {
        var machineId = MachineId.New();
        var processorId = new OperationalMetricProjectionProcessorId("metrics-a");
        var day = new ProductionDayId(new SiteId("site-a"), new DateOnly(2026, 8, 29));
        var summary = Summary(
            processorId,
            machineId,
            new OperationalMetricPeriodId.ProductionDay(day));
        var token = new ReportingContinuationToken("next-page");
        var reader = new OperationalMetricQueryReader(
            new StubReportingReader(
                new ReportingPage<OperationalMetricProjectionSummary>([summary], token)));

        var page = await reader.ReadAsync(
            ProductionDayQuery(machineId, processorId),
            CancellationToken.None);

        var item = Assert.IsType<ProductionDayOperationalMetricQueryItem>(
            Assert.Single(page.Items));
        Assert.Equal(day, item.ProductionDayId);
        AssertCommon(summary, item);
        Assert.Same(token, page.ContinuationToken);
    }

    [Fact]
    public async Task MapsShiftSummaryToClosedShiftQueryItem()
    {
        var machineId = MachineId.New();
        var processorId = new OperationalMetricProjectionProcessorId("metrics-a");
        var startsAtUtc = new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);
        var occurrence = new ShiftOccurrenceId(
            new SiteId("site-a"),
            new ShiftScheduleAssignmentId("schedule-a"),
            new ShiftId("shift-a"),
            startsAtUtc,
            startsAtUtc.AddHours(8));
        var summary = Summary(
            processorId,
            machineId,
            new OperationalMetricPeriodId.Shift(occurrence));
        var reader = new OperationalMetricQueryReader(
            new StubReportingReader(
                new ReportingPage<OperationalMetricProjectionSummary>([summary], null)));

        var page = await reader.ReadAsync(
            ShiftQuery(machineId, processorId, startsAtUtc),
            CancellationToken.None);

        var item = Assert.IsType<ShiftOperationalMetricQueryItem>(Assert.Single(page.Items));
        Assert.Equal(occurrence, item.ShiftOccurrenceId);
        AssertCommon(summary, item);
        Assert.Null(page.ContinuationToken);
    }

    [Fact]
    public async Task PreCancelledReadDoesNotInvokeReportingReader()
    {
        var stub = new StubReportingReader(
            new ReportingPage<OperationalMetricProjectionSummary>([], null));
        var reader = new OperationalMetricQueryReader(stub);
        var machineId = MachineId.New();
        var processorId = new OperationalMetricProjectionProcessorId("metrics-a");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await reader.ReadAsync(
                ProductionDayQuery(machineId, processorId),
                cancellation.Token));

        Assert.Equal(0, stub.ReadCount);
    }

    private static void AssertCommon(
        OperationalMetricProjectionSummary expected,
        OperationalMetricQueryItem actual)
    {
        Assert.Equal(expected.ProcessorId, actual.ProcessorId);
        Assert.Equal(expected.Key.MachineId, actual.MachineId);
        Assert.Equal(expected.Key.ContextKey, actual.ContextKey);
        Assert.Equal(expected.Key.DefinitionId, actual.DefinitionId);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.Value, actual.Value);
        Assert.Equal(expected.Unit, actual.Unit);
        Assert.Equal(expected.ReasonCode, actual.ReasonCode);
        Assert.Equal(expected.ReasonOperandName, actual.ReasonOperandName);
        Assert.Equal(expected.SourceRevision, actual.SourceRevision);
    }

    private static OperationalMetricProjectionSummary Summary(
        OperationalMetricProjectionProcessorId processorId,
        MachineId machineId,
        OperationalMetricPeriodId periodId)
    {
        var revision = new MetricAggregationCheckpoint(
            new MetricAggregationProcessorId($"aggregate:{machineId.Value:D}"),
            MetricInputStreamId.ForMachine(machineId),
            new MetricInputPosition(10));

        return new OperationalMetricProjectionSummary(
            processorId,
            new OperationalMetricEvaluationKey(
                machineId,
                periodId,
                new OperationalMetricDefinitionId("OEE", "1.0"),
                OperationalMetricEvaluationContextKey.Unpartitioned),
            OperationalMetricEvaluationStatus.Calculated,
            0.72m,
            OperationalMetricUnits.Ratio,
            null,
            null,
            revision);
    }

    private static ProductionDayOperationalMetricReportQuery ProductionDayQuery(
        MachineId machineId,
        OperationalMetricProjectionProcessorId processorId) => new(
            new OperationalMetricReportingSourceSelection(
                [new OperationalMetricReportingSource(machineId, processorId)]),
            new DateOnly(2026, 8, 29),
            new DateOnly(2026, 8, 30),
            null,
            null,
            null,
            OperationalMetricReportOrder.PeriodAscending,
            new ReportingPageRequest(20));

    private static ShiftOperationalMetricReportQuery ShiftQuery(
        MachineId machineId,
        OperationalMetricProjectionProcessorId processorId,
        DateTimeOffset startsAtUtc) => new(
            new OperationalMetricReportingSourceSelection(
                [new OperationalMetricReportingSource(machineId, processorId)]),
            startsAtUtc,
            startsAtUtc.AddDays(1),
            null,
            null,
            null,
            OperationalMetricReportOrder.PeriodAscending,
            new ReportingPageRequest(20));

    private sealed class StubReportingReader(
        ReportingPage<OperationalMetricProjectionSummary> page) :
        IOperationalMetricReportingQueryReader
    {
        public int ReadCount { get; private set; }

        public ValueTask<ReportingPage<OperationalMetricProjectionSummary>> ReadAsync(
            OperationalMetricReportQuery query,
            CancellationToken cancellationToken)
        {
            ReadCount++;
            return ValueTask.FromResult(page);
        }
    }
}
