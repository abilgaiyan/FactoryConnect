using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using Xunit;

namespace FactoryConnect.Core.Tests;

public sealed class PlannedProductionAllocationHardeningTests
{
    [Fact]
    public async Task ReplacementOverrideActivatesInactiveDay()
    {
        var date = new DateOnly(2026, 8, 29);
        var assignment = CreateSiteAssignment(date) with
        {
            ActiveDays = new HashSet<DayOfWeek>
            {
                DayOfWeek.Monday,
                DayOfWeek.Tuesday,
                DayOfWeek.Wednesday,
                DayOfWeek.Thursday,
                DayOfWeek.Friday,
            },
        };
        var calendarOverride = new PlannedProductionCalendarOverride
        {
            SiteId = assignment.SiteId,
            FactoryDate = date,
            ReplacementPlannedWindows =
            [
                Window(8, 0, 12, 0),
            ],
        };
        var resolver = new PlannedProductionIntervalResolver(
            new InMemoryPlannedProductionScheduleReader([assignment], [calendarOverride]));

        var result = await resolver.ResolveAsync(
            assignment.SiteId,
            date,
            date.AddDays(1),
            CancellationToken.None);

        Assert.Single(result);
    }

    [Fact]
    public async Task SiteFallbackForLinePreservesResolvedLineScope()
    {
        var date = new DateOnly(2026, 8, 27);
        var assignment = CreateSiteAssignment(date);
        var lineId = new ProductionLineId("LINE-1");
        var resolver = new PlannedProductionIntervalResolver(
            new InMemoryPlannedProductionScheduleReader([assignment]));

        var result = await resolver.ResolveAsync(
            assignment.SiteId,
            lineId,
            date,
            date.AddDays(1),
            CancellationToken.None);

        var interval = Assert.Single(result);
        Assert.Equal(lineId, interval.ProductionLineId);
        Assert.Equal(assignment.Id, interval.SourceAssignmentId);
    }

    [Fact]
    public async Task SiteWideResolutionPreservesSiteScope()
    {
        var date = new DateOnly(2026, 8, 27);
        var assignment = CreateSiteAssignment(date);
        var resolver = new PlannedProductionIntervalResolver(
            new InMemoryPlannedProductionScheduleReader([assignment]));

        var result = await resolver.ResolveAsync(
            assignment.SiteId,
            date,
            date.AddDays(1),
            CancellationToken.None);

        Assert.Null(Assert.Single(result).ProductionLineId);
    }

    [Fact]
    public async Task LineReplacementOverrideDoesNotApplyToAnotherLine()
    {
        var date = new DateOnly(2026, 8, 29);
        var assignment = CreateSiteAssignment(date) with
        {
            ActiveDays = new HashSet<DayOfWeek> { DayOfWeek.Monday },
        };
        var lineOne = new ProductionLineId("LINE-1");
        var lineTwo = new ProductionLineId("LINE-2");
        var calendarOverride = new PlannedProductionCalendarOverride
        {
            SiteId = assignment.SiteId,
            ProductionLineId = lineOne,
            FactoryDate = date,
            ReplacementPlannedWindows = [Window(8, 0, 12, 0)],
        };
        var resolver = new PlannedProductionIntervalResolver(
            new InMemoryPlannedProductionScheduleReader([assignment], [calendarOverride]));

        var lineOneResult = await resolver.ResolveAsync(
            assignment.SiteId,
            lineOne,
            date,
            date.AddDays(1),
            CancellationToken.None);
        var lineTwoResult = await resolver.ResolveAsync(
            assignment.SiteId,
            lineTwo,
            date,
            date.AddDays(1),
            CancellationToken.None);

        Assert.Equal(lineOne, Assert.Single(lineOneResult).ProductionLineId);
        Assert.Empty(lineTwoResult);
    }

    [Fact]
    public void AssignmentRejectsOverlappingPlannedWindows()
    {
        var date = new DateOnly(2026, 8, 27);
        var assignment = CreateSiteAssignment(date) with
        {
            PlannedWindows =
            [
                Window(6, 0, 12, 0),
                Window(10, 0, 14, 0),
            ],
        };

        Assert.Throws<ArgumentException>(assignment.Validate);
    }

    [Fact]
    public void AssignmentAllowsAdjacentPlannedWindows()
    {
        var date = new DateOnly(2026, 8, 27);
        var assignment = CreateSiteAssignment(date) with
        {
            PlannedWindows =
            [
                Window(6, 0, 10, 0),
                Window(10, 0, 14, 0),
            ],
        };

        assignment.Validate();
    }

    [Fact]
    public void AssignmentRejectsOvernightOverlapWithEarlyWindow()
    {
        var date = new DateOnly(2026, 8, 27);
        var assignment = CreateSiteAssignment(date) with
        {
            PlannedWindows =
            [
                Window(22, 0, 6, 0),
                Window(5, 0, 8, 0),
            ],
        };

        Assert.Throws<ArgumentException>(assignment.Validate);
    }

    [Fact]
    public void OverrideRejectsOverlappingReplacementWindows()
    {
        var calendarOverride = new PlannedProductionCalendarOverride
        {
            SiteId = new SiteId("SITE-1"),
            FactoryDate = new DateOnly(2026, 8, 27),
            ReplacementPlannedWindows =
            [
                Window(6, 0, 12, 0),
                Window(11, 0, 14, 0),
            ],
        };

        Assert.Throws<ArgumentException>(calendarOverride.Validate);
    }

    [Fact]
    public async Task ReplacementWindowsRetainRecurringBreakWindows()
    {
        var date = new DateOnly(2026, 8, 29);
        var assignment = CreateSiteAssignment(date) with
        {
            ActiveDays = new HashSet<DayOfWeek> { DayOfWeek.Monday },
            BreakWindows = [Window(10, 0, 10, 30)],
        };
        var calendarOverride = new PlannedProductionCalendarOverride
        {
            SiteId = assignment.SiteId,
            FactoryDate = date,
            ReplacementPlannedWindows = [Window(8, 0, 12, 0)],
        };
        var resolver = new PlannedProductionIntervalResolver(
            new InMemoryPlannedProductionScheduleReader([assignment], [calendarOverride]));

        var result = await resolver.ResolveAsync(
            assignment.SiteId,
            date,
            date.AddDays(1),
            CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal(TimeSpan.FromHours(2), result[0].Duration);
        Assert.Equal(TimeSpan.FromMinutes(90), result[1].Duration);
    }

    [Fact]
    public async Task ResolverRejectsDuplicateProviderAssignmentIds()
    {
        var date = new DateOnly(2026, 8, 27);
        var first = CreateSiteAssignment(date);
        var duplicate = first with
        {
            ProductionLineId = new ProductionLineId("LINE-1"),
        };
        var resolver = new PlannedProductionIntervalResolver(
            new StubReader([first, duplicate], []));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync(
                first.SiteId,
                date,
                date.AddDays(1),
                CancellationToken.None));
    }

    [Fact]
    public async Task ResolverRejectsProviderAssignmentOutsideRequestedInterval()
    {
        var date = new DateOnly(2026, 8, 27);
        var outside = CreateSiteAssignment(date.AddDays(2));
        var resolver = new PlannedProductionIntervalResolver(
            new StubReader([outside], []));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync(
                outside.SiteId,
                date,
                date.AddDays(1),
                CancellationToken.None));
    }

    private static PlannedProductionScheduleAssignment CreateSiteAssignment(DateOnly effectiveFrom) =>
        new()
        {
            Id = new PlannedProductionScheduleAssignmentId("PLAN-1"),
            CompanyId = new CompanyId("COMP-1"),
            SiteId = new SiteId("SITE-1"),
            TimeZoneId = new FactoryTimeZoneId("Asia/Kolkata"),
            EffectiveFrom = effectiveFrom,
            PlannedWindows = [Window(6, 0, 14, 0)],
        };

    private static PlannedProductionWindow Window(
        int startHour,
        int startMinute,
        int endHour,
        int endMinute) =>
        new()
        {
            StartsAtLocal = new TimeOnly(startHour, startMinute),
            EndsAtLocal = new TimeOnly(endHour, endMinute),
        };

    private sealed class StubReader : IPlannedProductionScheduleReader
    {
        private readonly IReadOnlyList<PlannedProductionScheduleAssignment> _assignments;
        private readonly IReadOnlyList<PlannedProductionCalendarOverride> _overrides;

        public StubReader(
            IReadOnlyList<PlannedProductionScheduleAssignment> assignments,
            IReadOnlyList<PlannedProductionCalendarOverride> overrides)
        {
            _assignments = assignments;
            _overrides = overrides;
        }

        public Task<IReadOnlyList<PlannedProductionScheduleAssignment>> ReadAssignmentsAsync(
            SiteId siteId,
            DateOnly factoryDateFrom,
            DateOnly factoryDateTo,
            CancellationToken cancellationToken) =>
            Task.FromResult(_assignments);

        public Task<IReadOnlyList<PlannedProductionCalendarOverride>> ReadOverridesAsync(
            SiteId siteId,
            DateOnly factoryDateFrom,
            DateOnly factoryDateTo,
            CancellationToken cancellationToken) =>
            Task.FromResult(_overrides);
    }
}
