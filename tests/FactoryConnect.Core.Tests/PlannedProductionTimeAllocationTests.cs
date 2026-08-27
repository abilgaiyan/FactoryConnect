using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using Xunit;

namespace FactoryConnect.Core.Tests;

public sealed class PlannedProductionTimeAllocationTests
{
    [Fact]
    public async Task ResolverSubtractsPlannedBreakFromProductionWindow()
    {
        var date = new DateOnly(2026, 8, 27);
        var siteId = new SiteId("SITE-1");
        var assignment = CreateSchedule(date, siteId, null,
            [new PlannedProductionWindow { StartsAtLocal = new TimeOnly(6, 0), EndsAtLocal = new TimeOnly(14, 0) }],
            [new PlannedProductionWindow { StartsAtLocal = new TimeOnly(10, 0), EndsAtLocal = new TimeOnly(10, 30) }]);
        var resolver = new PlannedProductionIntervalResolver(
            new InMemoryPlannedProductionScheduleReader([assignment]));

        var result = await resolver.ResolveAsync(siteId, date, date.AddDays(1), CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal(TimeSpan.FromHours(4), result[0].Duration);
        Assert.Equal(TimeSpan.FromHours(3.5), result[1].Duration);
    }

    [Fact]
    public async Task ResolverHonorsShutdownOverride()
    {
        var date = new DateOnly(2026, 8, 27);
        var siteId = new SiteId("SITE-1");
        var assignment = CreateSchedule(date, siteId, null,
            [new PlannedProductionWindow { StartsAtLocal = new TimeOnly(6, 0), EndsAtLocal = new TimeOnly(14, 0) }], []);
        var calendarOverride = new PlannedProductionCalendarOverride
        {
            SiteId = siteId,
            FactoryDate = date,
            IsShutdown = true,
        };
        var resolver = new PlannedProductionIntervalResolver(
            new InMemoryPlannedProductionScheduleReader([assignment], [calendarOverride]));

        var result = await resolver.ResolveAsync(siteId, date, date.AddDays(1), CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task ResolverUsesLineSpecificScheduleBeforeSiteFallback()
    {
        var date = new DateOnly(2026, 8, 27);
        var siteId = new SiteId("SITE-1");
        var lineId = new ProductionLineId("LINE-1");
        var site = CreateSchedule(date, siteId, null,
            [new PlannedProductionWindow { StartsAtLocal = new TimeOnly(6, 0), EndsAtLocal = new TimeOnly(14, 0) }], []);
        var line = CreateSchedule(date, siteId, lineId,
            [new PlannedProductionWindow { StartsAtLocal = new TimeOnly(7, 0), EndsAtLocal = new TimeOnly(15, 0) }], []);
        var resolver = new PlannedProductionIntervalResolver(
            new InMemoryPlannedProductionScheduleReader([site, line]));

        var result = await resolver.ResolveAsync(siteId, lineId, date, date.AddDays(1), CancellationToken.None);

        var interval = Assert.Single(result);
        Assert.Equal(line.Id, interval.SourceAssignmentId);
        Assert.Equal(lineId, interval.ProductionLineId);
    }

    [Fact]
    public void AllocatorSplitsPlannedAndNonPlannedEligibility()
    {
        var start = new DateTimeOffset(2026, 8, 27, 6, 0, 0, TimeSpan.Zero);
        var source = CreateContextualized(start, start.AddHours(4));
        var planned = CreatePlanned(start.AddHours(1), start.AddHours(3));

        var result = ProductionTimeEligibilityAllocator.Allocate(source, [planned]);

        Assert.Equal(3, result.Count);
        Assert.False(result[0].IsPlannedProductionTime);
        Assert.True(result[1].IsPlannedProductionTime);
        Assert.False(result[2].IsPlannedProductionTime);
        Assert.Equal(source.Duration, result.Aggregate(TimeSpan.Zero, static (sum, item) => sum + item.Duration));
    }

    [Fact]
    public void AllocatorPreservesHierarchyContextAndLineage()
    {
        var start = new DateTimeOffset(2026, 8, 27, 6, 0, 0, TimeSpan.Zero);
        var source = CreateContextualized(start, start.AddHours(2));
        var planned = CreatePlanned(start, start.AddHours(2));

        var result = ProductionTimeEligibilityAllocator.Allocate(source, [planned]);

        var interval = Assert.Single(result);
        Assert.Equal(source.Id, interval.SourceContextualizedActivityIntervalId);
        Assert.Equal(source.CompanyId, interval.CompanyId);
        Assert.Equal(source.SiteId, interval.SiteId);
        Assert.Equal(source.ProductionLineId, interval.ProductionLineId);
        Assert.Equal(source.MachineId, interval.MachineId);
        Assert.Equal(source.ProductionContextAssignmentId, interval.ProductionContextAssignmentId);
        Assert.Equal(source.PartId, interval.PartId);
        Assert.True(interval.IsPlannedProductionTime);
    }

    [Fact]
    public void AllocatorProducesDeterministicIdsAcrossReplay()
    {
        var start = new DateTimeOffset(2026, 8, 27, 6, 0, 0, TimeSpan.Zero);
        var source = CreateContextualized(start, start.AddHours(2));
        var planned = CreatePlanned(start, start.AddHours(1));

        var first = ProductionTimeEligibilityAllocator.Allocate(source, [planned]);
        var second = ProductionTimeEligibilityAllocator.Allocate(source, [planned]);

        Assert.Equal(first.Select(static item => item.Id), second.Select(static item => item.Id));
    }

    private static PlannedProductionScheduleAssignment CreateSchedule(
        DateOnly effectiveFrom,
        SiteId siteId,
        ProductionLineId? lineId,
        IReadOnlyList<PlannedProductionWindow> planned,
        IReadOnlyList<PlannedProductionWindow> breaks) =>
        new()
        {
            Id = new PlannedProductionScheduleAssignmentId(lineId?.Value ?? "SITE"),
            CompanyId = new CompanyId("COMP-1"),
            SiteId = siteId,
            ProductionLineId = lineId,
            TimeZoneId = new FactoryTimeZoneId("Asia/Kolkata"),
            EffectiveFrom = effectiveFrom,
            PlannedWindows = planned,
            BreakWindows = breaks,
        };

    private static ContextualizedActivityInterval CreateContextualized(
        DateTimeOffset startsAt,
        DateTimeOffset endsAt) =>
        new()
        {
            Id = new ContextualizedActivityIntervalId("CTX-1"),
            SourceProcessorId = new ObservationProcessorId("activity-projection"),
            SourcePosition = new ObservationPosition(1),
            SourceStreamId = new ObservationStreamId(MachineId.New(), "stream-1"),
            SourceInstanceId = 1,
            SourceSequence = 1,
            CompanyId = new CompanyId("COMP-1"),
            SiteId = new SiteId("SITE-1"),
            ProductionLineId = new ProductionLineId("LINE-1"),
            MachineId = MachineId.New(),
            State = MachineState.Running,
            StartsAtUtc = startsAt,
            EndsAtUtc = endsAt,
            ShiftId = new ShiftId("SHIFT-1"),
            ShiftScheduleAssignmentId = new ShiftScheduleAssignmentId("SHIFT-A"),
            ProductionContextAssignmentId = new ProductionContextAssignmentId("CTX-A"),
            PartId = new PartId("PART-1"),
        };

    private static PlannedProductionInterval CreatePlanned(
        DateTimeOffset startsAt,
        DateTimeOffset endsAt) =>
        new()
        {
            SourceAssignmentId = new PlannedProductionScheduleAssignmentId("PLAN-1"),
            CompanyId = new CompanyId("COMP-1"),
            SiteId = new SiteId("SITE-1"),
            ProductionLineId = new ProductionLineId("LINE-1"),
            FactoryDate = DateOnly.FromDateTime(startsAt.UtcDateTime),
            StartsAtUtc = startsAt,
            EndsAtUtc = endsAt,
        };
}
