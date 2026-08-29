using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Tests;

public sealed class OperationalMetricReportQueryContractTests
{
    private static readonly OperationalMetricProjectionProcessorId ProcessorId = new("metrics-v1");

    [Fact]
    public void ShiftQueryPreservesTypedHalfOpenUtcRangeAndExactSelections()
    {
        var machineId = MachineId.New();
        var definitionId = new OperationalMetricDefinitionId("OEE", "1.0");
        var start = new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddDays(1);
        var context = new OperationalMetricContextFilter
        {
            PartId = new PartId("part-1"),
        };

        var query = new ShiftOperationalMetricReportQuery(
            ProcessorId,
            new ReportingMachineSelection([machineId]),
            start,
            end,
            new OperationalMetricDefinitionSelection([definitionId]),
            context,
            new OperationalMetricStatusSelection([OperationalMetricEvaluationStatus.Calculated]),
            OperationalMetricReportOrder.PeriodDescending,
            new ReportingPageRequest(50, new ReportingContinuationToken("next-page")));

        Assert.Equal(start, query.StartsAtOrAfterUtc);
        Assert.Equal(end, query.StartsBeforeUtc);
        Assert.Equal(machineId, Assert.Single(query.Machines.MachineIds));
        Assert.Equal(definitionId, Assert.Single(query.Metrics!.DefinitionIds));
        Assert.Equal(context, query.Context);
        Assert.Equal(
            OperationalMetricEvaluationStatus.Calculated,
            Assert.Single(query.Statuses!.Statuses));
        Assert.Equal(OperationalMetricReportOrder.PeriodDescending, query.Order);
        Assert.Equal(50, query.Page.PageSize);
        Assert.Equal("next-page", query.Page.ContinuationToken!.Value);
    }

    [Fact]
    public void ProductionDayQueryCannotRepresentShiftPeriodSelection()
    {
        var from = new DateOnly(2026, 8, 1);
        var to = new DateOnly(2026, 9, 1);

        OperationalMetricReportQuery query = new ProductionDayOperationalMetricReportQuery(
            ProcessorId,
            new ReportingMachineSelection([MachineId.New()]),
            from,
            to,
            null,
            null,
            null,
            OperationalMetricReportOrder.PeriodAscending,
            new ReportingPageRequest(100));

        var productionDayQuery = Assert.IsType<ProductionDayOperationalMetricReportQuery>(query);
        Assert.Equal(from, productionDayQuery.FromInclusive);
        Assert.Equal(to, productionDayQuery.ToExclusive);
    }

    [Fact]
    public void MachineSelectionRequiresUniqueNonEmptyMachines()
    {
        var machineId = MachineId.New();

        Assert.Throws<ArgumentException>(() => new ReportingMachineSelection([]));
        Assert.Throws<ArgumentException>(() => new ReportingMachineSelection([default]));
        Assert.Throws<ArgumentException>(() => new ReportingMachineSelection([machineId, machineId]));
    }

    [Fact]
    public void MachineSelectionSnapshotsCallerCollection()
    {
        var machines = new List<MachineId> { MachineId.New() };
        var selection = new ReportingMachineSelection(machines);

        machines.Add(MachineId.New());

        Assert.Single(selection.MachineIds);
    }

    [Fact]
    public void MetricSelectionRequiresUniqueExactDefinitionVersions()
    {
        var v1 = new OperationalMetricDefinitionId("Availability", "1.0");
        var v2 = new OperationalMetricDefinitionId("Availability", "2.0");

        var selection = new OperationalMetricDefinitionSelection([v1, v2]);

        Assert.Equal([v1, v2], selection.DefinitionIds);
        Assert.Throws<ArgumentException>(() => new OperationalMetricDefinitionSelection([]));
        Assert.Throws<ArgumentException>(() => new OperationalMetricDefinitionSelection([v1, v1]));
        Assert.Throws<ArgumentException>(() =>
            new OperationalMetricDefinitionSelection([v1, null!]));
    }

    [Fact]
    public void StatusSelectionRequiresUniqueSupportedValues()
    {
        Assert.Throws<ArgumentException>(() => new OperationalMetricStatusSelection([]));
        Assert.Throws<ArgumentException>(() => new OperationalMetricStatusSelection(
        [
            OperationalMetricEvaluationStatus.Calculated,
            OperationalMetricEvaluationStatus.Calculated,
        ]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OperationalMetricStatusSelection(
            [(OperationalMetricEvaluationStatus)999]));
    }

    [Fact]
    public void ContextFilterMatchesEverySpecifiedCanonicalField()
    {
        var filter = new OperationalMetricContextFilter
        {
            ProductionOrderId = new ProductionOrderId("order-1"),
            PartId = new PartId("part-1"),
        };
        var matchingContext = new OperationalMetricEvaluationContextKey
        {
            ProductionOrderId = new ProductionOrderId("order-1"),
            OperationId = new OperationId("operation-1"),
            PartId = new PartId("part-1"),
            OperatorId = new OperatorId("operator-1"),
        };
        var wrongPartContext = matchingContext with { PartId = new PartId("part-2") };

        Assert.True(filter.Matches(matchingContext));
        Assert.False(filter.Matches(wrongPartContext));
        Assert.True(new OperationalMetricContextFilter().Matches(matchingContext));
    }

    [Fact]
    public void ContextFilterRejectsSpecifiedEmptyIdentity()
    {
        var filter = new OperationalMetricContextFilter
        {
            OperatorId = default(OperatorId),
        };

        Assert.Throws<ArgumentException>(filter.Validate);
    }

    [Fact]
    public void ShiftRangeRequiresUtcHalfOpenBoundaries()
    {
        var start = new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);

        Assert.Throws<ArgumentException>(() => ShiftQuery(start, start));
        Assert.Throws<ArgumentException>(() => ShiftQuery(start, start.AddMinutes(-1)));
        Assert.Throws<ArgumentException>(() => ShiftQuery(start.ToOffset(TimeSpan.FromHours(5.5)), start.AddDays(1)));
        Assert.Throws<ArgumentException>(() => ShiftQuery(start, start.AddDays(1).ToOffset(TimeSpan.FromHours(5.5))));
    }

    [Fact]
    public void ProductionDayRangeRequiresNonDefaultHalfOpenBoundaries()
    {
        var day = new DateOnly(2026, 8, 29);

        Assert.Throws<ArgumentOutOfRangeException>(() => ProductionDayQuery(default, day));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProductionDayQuery(day, default));
        Assert.Throws<ArgumentException>(() => ProductionDayQuery(day, day));
        Assert.Throws<ArgumentException>(() => ProductionDayQuery(day, day.AddDays(-1)));
    }

    [Fact]
    public void PageRequestEnforcesBoundedSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReportingPageRequest(0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ReportingPageRequest(ReportingPageRequest.MaximumPageSize + 1));

        Assert.Equal(
            ReportingPageRequest.MaximumPageSize,
            new ReportingPageRequest(ReportingPageRequest.MaximumPageSize).PageSize);
    }

    [Fact]
    public void ContinuationTokenIsRequiredAndPreservedExactly()
    {
        Assert.Throws<ArgumentException>(() => new ReportingContinuationToken(" "));
        Assert.Equal(" opaque-token ", new ReportingContinuationToken(" opaque-token ").Value);
    }

    [Fact]
    public void ProductionDayOrderingDefinesCanonicalTieBreakersForEveryEvaluationIdentity()
    {
        var machineA = new MachineId(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var machineB = new MachineId(Guid.Parse("00000000-0000-0000-0000-000000000002"));
        var day = new DateOnly(2026, 8, 29);
        var nextDay = day.AddDays(1);
        var availability = new OperationalMetricDefinitionId("Availability", "1.0");
        var availabilityV2 = new OperationalMetricDefinitionId("Availability", "2.0");
        var oee = new OperationalMetricDefinitionId("OEE", "1.0");
        var partitioned = new OperationalMetricEvaluationContextKey
        {
            ProductionOrderId = new ProductionOrderId("order-1"),
        };
        var expected = new[]
        {
            EvaluationKey(machineA, "site-a", day, availability),
            EvaluationKey(machineA, "site-a", day, availabilityV2),
            EvaluationKey(machineA, "site-a", day, oee),
            EvaluationKey(machineA, "site-a", day, availability, partitioned),
            EvaluationKey(machineA, "site-b", day, availability),
            EvaluationKey(machineB, "site-a", day, availability),
            EvaluationKey(machineA, "site-a", nextDay, availability),
        };
        var shuffled = new[]
        {
            expected[4],
            expected[2],
            expected[6],
            expected[1],
            expected[5],
            expected[0],
            expected[3],
        };

        Array.Sort(
            shuffled,
            OperationalMetricReportOrdering.GetEvaluationKeyComparer(
                OperationalMetricReportOrder.PeriodAscending));

        Assert.Equal(expected, shuffled);
        Assert.All(
            expected.Zip(expected.Skip(1)),
            pair => Assert.True(
                OperationalMetricReportOrdering.GetEvaluationKeyComparer(
                    OperationalMetricReportOrder.PeriodAscending)
                .Compare(pair.First, pair.Second) < 0));
    }

    [Fact]
    public void ShiftOrderingUsesCompleteOccurrenceIdentityAfterPeriodAndMachine()
    {
        var machineId = new MachineId(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var start = new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);
        var definition = new OperationalMetricDefinitionId("OEE", "1.0");
        var scheduleA = ShiftEvaluationKey(machineId, "schedule-a", start, definition);
        var scheduleB = ShiftEvaluationKey(machineId, "schedule-b", start, definition);
        var values = new[] { scheduleB, scheduleA };

        Array.Sort(
            values,
            OperationalMetricReportOrdering.GetEvaluationKeyComparer(
                OperationalMetricReportOrder.PeriodAscending));

        Assert.Equal([scheduleA, scheduleB], values);
    }

    [Fact]
    public void DescendingPeriodOrderingRetainsAscendingCanonicalTieBreakers()
    {
        var machineA = new MachineId(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var machineB = new MachineId(Guid.Parse("00000000-0000-0000-0000-000000000002"));
        var day = new DateOnly(2026, 8, 29);
        var definition = new OperationalMetricDefinitionId("OEE", "1.0");
        var earlierA = EvaluationKey(machineA, "site-a", day, definition);
        var earlierB = EvaluationKey(machineB, "site-a", day, definition);
        var laterA = EvaluationKey(machineA, "site-a", day.AddDays(1), definition);
        var values = new[] { earlierB, earlierA, laterA };

        Array.Sort(
            values,
            OperationalMetricReportOrdering.GetEvaluationKeyComparer(
                OperationalMetricReportOrder.PeriodDescending));

        Assert.Equal([laterA, earlierA, earlierB], values);
    }

    [Fact]
    public void CanonicalOrderingRejectsMixedPeriodScopes()
    {
        var machineId = MachineId.New();
        var definition = new OperationalMetricDefinitionId("OEE", "1.0");
        var productionDay = EvaluationKey(
            machineId,
            "site-a",
            new DateOnly(2026, 8, 29),
            definition);
        var shift = new ShiftOccurrenceId(
            new SiteId("site-a"),
            new ShiftScheduleAssignmentId("schedule-a"),
            new ShiftId("shift-a"),
            new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 29, 8, 0, 0, TimeSpan.Zero));
        var shiftKey = new OperationalMetricEvaluationKey(
            machineId,
            new OperationalMetricPeriodId.Shift(shift),
            definition,
            OperationalMetricEvaluationContextKey.Unpartitioned);

        Assert.Throws<ArgumentException>(() =>
            OperationalMetricReportOrdering.GetEvaluationKeyComparer(
                OperationalMetricReportOrder.PeriodAscending)
            .Compare(productionDay, shiftKey));
    }

    [Fact]
    public void ReportingPageSnapshotsItemsAndRejectsNulls()
    {
        var items = new List<string> { "one" };
        var page = new ReportingPage<string>(items, new ReportingContinuationToken("next"));

        items.Add("two");

        Assert.Equal("one", Assert.Single(page.Items));
        Assert.Equal("next", page.ContinuationToken!.Value);
        Assert.Throws<ArgumentException>(() => new ReportingPage<string>(["one", null!], null));
    }

    [Fact]
    public void QueryRejectsUnsupportedOrdering()
    {
        var start = new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);

        Assert.Throws<ArgumentOutOfRangeException>(() => new ShiftOperationalMetricReportQuery(
            ProcessorId,
            new ReportingMachineSelection([MachineId.New()]),
            start,
            start.AddDays(1),
            null,
            null,
            null,
            (OperationalMetricReportOrder)999,
            new ReportingPageRequest(10)));
    }

    private static ShiftOperationalMetricReportQuery ShiftQuery(
        DateTimeOffset start,
        DateTimeOffset end) =>
        new(
            ProcessorId,
            new ReportingMachineSelection([MachineId.New()]),
            start,
            end,
            null,
            null,
            null,
            OperationalMetricReportOrder.PeriodAscending,
            new ReportingPageRequest(10));

    private static ProductionDayOperationalMetricReportQuery ProductionDayQuery(
        DateOnly from,
        DateOnly to) =>
        new(
            ProcessorId,
            new ReportingMachineSelection([MachineId.New()]),
            from,
            to,
            null,
            null,
            null,
            OperationalMetricReportOrder.PeriodAscending,
            new ReportingPageRequest(10));

    private static OperationalMetricEvaluationKey EvaluationKey(
        MachineId machineId,
        string siteId,
        DateOnly businessDate,
        OperationalMetricDefinitionId definitionId,
        OperationalMetricEvaluationContextKey? contextKey = null) =>
        new(
            machineId,
            new OperationalMetricPeriodId.ProductionDay(
                new ProductionDayId(new SiteId(siteId), businessDate)),
            definitionId,
            contextKey ?? OperationalMetricEvaluationContextKey.Unpartitioned);

    private static OperationalMetricEvaluationKey ShiftEvaluationKey(
        MachineId machineId,
        string scheduleId,
        DateTimeOffset startsAtUtc,
        OperationalMetricDefinitionId definitionId) =>
        new(
            machineId,
            new OperationalMetricPeriodId.Shift(
                new ShiftOccurrenceId(
                    new SiteId("site-a"),
                    new ShiftScheduleAssignmentId(scheduleId),
                    new ShiftId("shift-a"),
                    startsAtUtc,
                    startsAtUtc.AddHours(8))),
            definitionId,
            OperationalMetricEvaluationContextKey.Unpartitioned);
}
