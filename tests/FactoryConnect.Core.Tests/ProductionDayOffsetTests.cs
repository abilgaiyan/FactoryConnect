using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using Xunit;

namespace FactoryConnect.Core.Tests;

public sealed class ProductionDayOffsetTests
{
    private static readonly DateOnly Day = new(2026, 10, 22);
    private static readonly SiteId Site = new("GAJRA");

    [Fact]
    public async Task GajraDayPreservesOwnershipAndNetShiftMinutes()
    {
        var shifts = Shifts();
        var resolver = new ShiftOccurrenceResolver(new InMemoryShiftScheduleReader(shifts));
        var occurrences = await resolver.ResolveAsync(Site, Day, Day.AddDays(2), CancellationToken.None);
        Assert.Equal(6, occurrences.Count);
        var planned = await Planned(Schedule()).ResolveAsync(Site, Day, Day.AddDays(1), CancellationToken.None);
        Assert.All(planned, item => Assert.Equal(Day, item.FactoryDate));
        Assert.Equal(1350, planned.Sum(item => item.Duration.TotalMinutes));
        var expected = new[] { 480d, 450d, 420d };
        for (var index = 0; index < 3; index++)
        {
            var shift = occurrences[index];
            Assert.Equal(Day, shift.FactoryDate);
            var minutes = planned.Sum(item => Math.Max(0,
                ((item.EndsAtUtc < shift.EndsAtUtc ? item.EndsAtUtc : shift.EndsAtUtc) -
                 (item.StartsAtUtc > shift.StartsAtUtc ? item.StartsAtUtc : shift.StartsAtUtc)).TotalMinutes));
            Assert.Equal(expected[index], minutes);
        }
        Assert.Equal(1440, occurrences.Take(3).Sum(item => (item.EndsAtUtc - item.StartsAtUtc).TotalMinutes));
        for (var index = 1; index < occurrences.Count; index++)
            Assert.Equal(occurrences[index - 1].EndsAtUtc, occurrences[index].StartsAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 18, 30, 0, TimeSpan.Zero), occurrences[2].StartsAtUtc);
        Assert.Equal(occurrences[2].EndsAtUtc, occurrences[3].StartsAtUtc);
        Assert.Equal(Day.AddDays(1), occurrences[3].FactoryDate);
    }

    [Fact]
    public async Task ReplacementIgnoresExcludedBreakAndClipsIntersectingBreakOnce()
    {
        var replacement = new PlannedProductionCalendarOverride
        {
            SiteId = Site, FactoryDate = Day,
            ReplacementPlannedWindows = [Window(7, 30, 12, 15)],
        };
        var reader = new InMemoryPlannedProductionScheduleReader([Schedule()], [replacement]);
        var result = await new PlannedProductionIntervalResolver(reader)
            .ResolveAsync(Site, Day, Day.AddDays(1), CancellationToken.None);
        Assert.Equal(270, result.Sum(item => item.Duration.TotalMinutes));
    }

    [Fact]
    public async Task EffectiveTransitionDetectsDayTwoCollisionOutsideRequestedDate()
    {
        var old = Schedule() with
        {
            EffectiveTo = Day.AddDays(1), BreakWindows = [],
            PlannedWindows = [Window(23, 0, 2, 0, 1)],
        };
        var next = Schedule() with
        {
            Id = new PlannedProductionScheduleAssignmentId("NEXT"), EffectiveFrom = Day.AddDays(1),
            BreakWindows = [], PlannedWindows = [Window(1, 0, 3, 0, 1)],
        };
        var resolver = new PlannedProductionIntervalResolver(new InMemoryPlannedProductionScheduleReader([old, next]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(Site, Day, Day.AddDays(1), CancellationToken.None));
    }

    [Fact]
    public void ShiftEffectiveTransitionDetectsDayTwoCollision()
    {
        var old = Shifts()[0] with { StartsAtLocal = new TimeOnly(23, 0), EndsAtLocal = new TimeOnly(2, 0), StartDayOffset = 1, EffectiveTo = Day.AddDays(1) };
        var next = old with { Id = new ShiftScheduleAssignmentId("NEXT"), EffectiveFrom = Day.AddDays(1), EffectiveTo = null, StartsAtLocal = new TimeOnly(1, 0), EndsAtLocal = new TimeOnly(3, 0) };
        Assert.Throws<InvalidOperationException>(() => new InMemoryShiftScheduleReader([old, next]));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void OffsetsAreBounded(int offset)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => (Window(7, 0, 8, 0) with { StartDayOffset = offset }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (Shifts()[0] with { StartDayOffset = offset }).Validate());
    }

    [Fact]
    public void LegacyOvernightBreakIsInferredButExplicitZeroIsRespected()
    {
        var planned = new[] { Window(22, 0, 6, 0) };
        var pause = new PlannedProductionWindow { StartsAtLocal = new TimeOnly(3, 30), EndsAtLocal = new TimeOnly(4, 0) };
        Assert.Equal(1, Assert.Single(PlannedProductionBreakPlacement.Resolve(planned, [pause])).StartDayOffset);
        Assert.Throws<InvalidOperationException>(() => PlannedProductionBreakPlacement.Resolve(planned, [pause with { StartDayOffset = 0 }]));
    }

    [Fact]
    public void AmbiguousBreakAndOverlappingBreaksAreRejected()
    {
        var pause = new PlannedProductionWindow { StartsAtLocal = new TimeOnly(8, 0), EndsAtLocal = new TimeOnly(8, 30) };
        Assert.Throws<InvalidOperationException>(() => PlannedProductionBreakPlacement.Resolve(
            [Window(7, 0, 9, 0), Window(7, 0, 9, 0, 1)], [pause]));
        Assert.Throws<InvalidOperationException>(() => PlannedProductionBreakPlacement.Resolve(
            [Window(7, 0, 9, 0)], [pause, pause]));
    }

    [Fact]
    public async Task FinalEffectiveBusinessDateRetainsFollowingMorning()
    {
        var assignment = Schedule() with { EffectiveTo = Day.AddDays(1) };
        var result = await Planned(assignment).ResolveAsync(Site, Day, Day.AddDays(1), CancellationToken.None);
        Assert.Equal(1350, result.Sum(item => item.Duration.TotalMinutes));
        Assert.All(result, item => Assert.Equal(Day, item.FactoryDate));
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 2, 0, 0, TimeSpan.Zero), result[^1].EndsAtUtc);
    }

    [Fact]
    public async Task HistoricalFollowingMorningActivityAndQuantityAppendMatchRosterOwner()
    {
        var machine = new MachineId(Guid.Parse("677b030b-1ede-4a67-82ae-6a494791357f"));
        var line = new ProductionLineId("LINE-1");
        var company = new CompanyId("G");
        var activityStream = new ObservationStreamId(machine, "activity");
        var quantityStream = new ObservationStreamId(machine, "quantity");
        var resolver = new ShiftOccurrenceResolver(new InMemoryShiftScheduleReader(
            Shifts().Select(item => item with { ProductionLineId = line })));
        var rosterStore = new InMemoryMachineShiftOccurrenceRosterStore();
        var scope = new MachineShiftScheduleScope(machine, Site, line);
        var dayId = new ProductionDayId(Site, Day);
        var roster = await new MachineShiftOccurrenceRosterMaterializer(resolver, rosterStore)
            .MaterializeAsync(scope, dayId, CancellationToken.None);
        Assert.Equal(3, roster.Occurrences.Count);
        var inner = new InMemoryProductionContextProcessingStore();
        var guarded = new RosterValidatedProductionContextProcessingStore(inner, rosterStore, [scope]);
        // 01:00 on October 23 in India, physically on October 22 UTC.
        var timestamp = new DateTimeOffset(2026, 10, 22, 19, 30, 0, TimeSpan.Zero);
        var activities = new InMemoryProductionContextActivityReader();
        activities.Add(new DurableMachineActivityPeriod(new ObservationProcessorId("activity-source"),
            new ObservationPosition(1), activityStream, 1, 1,
            new MachineActivityPeriod(machine, MachineState.Running, timestamp, timestamp.AddMinutes(10))));
        var contexts = new InMemoryProductionContextReader([new ProductionContextAssignment
        {
            Id = new ProductionContextAssignmentId("CTX"), CompanyId = company, SiteId = Site,
            ProductionLineId = line, MachineId = machine, EffectiveFrom = timestamp.AddDays(-1),
        }]);
        var activity = new ProductionContextProcessingRuntime(new ObservationProcessorId("activity"),
            activities, contexts, resolver, Planned(Schedule() with { ProductionLineId = line }), guarded,
            new ProductionContextProcessingScope
            {
                CompanyId = company, SiteId = Site, ProductionLineId = line, MachineId = machine, StreamId = activityStream,
            }, 10);
        Assert.Equal(1, await activity.RunCycleAsync());
        var quantities = new InMemoryProductionQuantityEvidenceReader();
        quantities.Add(new DurableProductionQuantityEvidence(new ObservationPosition(1), quantityStream,
            new ProductionQuantityEvidence
            {
                Id = new ProductionQuantityEvidenceId("Q"), CompanyId = company, SiteId = Site,
                ProductionLineId = line, MachineId = machine, ShiftId = new ShiftId("SHIFT-3"),
                OccurredAtUtc = timestamp, PartCountIncrement = 1, GoodQuantity = 1, RejectedQuantity = 0,
            }));
        var quantity = new ProductionQuantityFactProcessingRuntime(new ObservationProcessorId("quantity"),
            quantities, resolver, guarded, quantityStream, 10);
        Assert.Equal(1, await quantity.RunCycleAsync());
        Assert.NotEmpty(inner.PositionedMetricInputs);
        Assert.Contains(inner.PositionedMetricInputs, item => item.Fact.SourceQuantityEvidenceId is not null);
        Assert.Contains(inner.PositionedMetricInputs, item => item.Fact.SourceContextualizedActivityIntervalId is not null);
        Assert.All(inner.PositionedMetricInputs, item =>
        {
            Assert.Equal(dayId, item.ProductionDayId);
            Assert.Equal(roster.Occurrences[2].ShiftOccurrenceId, item.ShiftOccurrenceId);
        });
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    public void DisjointEffectiveRangesCompareLastOldOccurrenceInEitherInsertionOrder(int gap, bool reverse)
    {
        var old = Shifts()[0] with
        {
            EffectiveFrom = Day.AddMonths(-1), EffectiveTo = Day,
            StartsAtLocal = new TimeOnly(23, 0), EndsAtLocal = new TimeOnly(2, 0), StartDayOffset = 1,
        };
        var next = old with
        {
            Id = new ShiftScheduleAssignmentId("NEW"), EffectiveFrom = Day.AddDays(gap), EffectiveTo = null,
            StartsAtLocal = new TimeOnly(1, 0), EndsAtLocal = new TimeOnly(3, 0), StartDayOffset = gap == 0 ? 1 : 0,
        };
        Assert.Throws<InvalidOperationException>(() => new InMemoryShiftScheduleReader(reverse ? [next, old] : [old, next]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolverPaddingClampsAtCalendarLimits(bool upper)
    {
        var from = upper ? DateOnly.MaxValue.AddDays(-3) : DateOnly.MinValue;
        var to = upper ? DateOnly.MaxValue : DateOnly.MinValue.AddDays(3);
        var shift = Shifts()[0] with
        {
            EffectiveFrom = from, EffectiveTo = to, TimeZoneId = new FactoryTimeZoneId("UTC"),
            StartsAtLocal = new TimeOnly(8, 0), EndsAtLocal = new TimeOnly(9, 0),
        };
        var shifts = new ShiftOccurrenceResolver(new InMemoryShiftScheduleReader([shift]));
        var occurrences = await shifts.ResolveAsync(Site, from, to, CancellationToken.None);
        Assert.Equal(3, occurrences.Count);
        var planned = Planned(Schedule() with
        {
            EffectiveFrom = from, EffectiveTo = to, TimeZoneId = new FactoryTimeZoneId("UTC"),
            PlannedWindows = [Window(8, 0, 9, 0)], BreakWindows = [],
        });
        var intervals = await planned.ResolveAsync(Site, from, to, CancellationToken.None);
        Assert.Equal(3, intervals.Count);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => shifts.ResolveAsync(Site, to, from, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => planned.ResolveAsync(Site, from, from, CancellationToken.None));
    }

    [Fact]
    public async Task PlannedOverlapUsesSelectedLineAuthorityAndSiteFallback()
    {
        var lineId = new ProductionLineId("LINE-1");
        var site = Schedule() with
        {
            TimeZoneId = new FactoryTimeZoneId("UTC"), BreakWindows = [],
            PlannedWindows = [Window(8, 0, 10, 0)],
        };
        var line = site with
        {
            Id = new PlannedProductionScheduleAssignmentId("LINE"), ProductionLineId = lineId,
            EffectiveFrom = Day.AddDays(1), PlannedWindows = [Window(9, 0, 11, 0)],
        };
        var unrelated = line with
        {
            Id = new PlannedProductionScheduleAssignmentId("OTHER"), ProductionLineId = new ProductionLineId("LINE-2"),
            PlannedWindows = [Window(8, 0, 12, 0)],
        };
        var resolver = new PlannedProductionIntervalResolver(new InMemoryPlannedProductionScheduleReader([site, line, unrelated]));
        var result = await resolver.ResolveAsync(Site, lineId, Day, Day.AddDays(2), CancellationToken.None);
        Assert.Equal(2, result.Count);
        Assert.Equal(site.Id, result[0].SourceAssignmentId);
        Assert.Equal(line.Id, result[1].SourceAssignmentId);
        Assert.All(result, item => Assert.Equal(lineId, item.ProductionLineId));
        var siteOnly = await resolver.ResolveAsync(Site, Day, Day.AddDays(2), CancellationToken.None);
        Assert.All(siteOnly, item => Assert.Equal(site.Id, item.SourceAssignmentId));
    }

    [Fact]
    public async Task ShortenedReplacementCannotHideInvalidOrdinaryBreakOwnership()
    {
        var invalid = Schedule() with { BreakWindows = [Window(6, 0, 8, 0)] };
        var replacement = new PlannedProductionCalendarOverride
        {
            SiteId = Site, FactoryDate = Day, ReplacementPlannedWindows = [Window(9, 0, 10, 0)],
        };
        var resolver = new PlannedProductionIntervalResolver(new InMemoryPlannedProductionScheduleReader([invalid], [replacement]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(Site, Day, Day.AddDays(1), CancellationToken.None));
    }

    private static PlannedProductionIntervalResolver Planned(PlannedProductionScheduleAssignment schedule) =>
        new(new InMemoryPlannedProductionScheduleReader([schedule]));

    private static PlannedProductionWindow Window(int sh, int sm, int eh, int em, int offset = 0) => new()
    {
        StartsAtLocal = new TimeOnly(sh, sm), EndsAtLocal = new TimeOnly(eh, em), StartDayOffset = offset,
    };

    private static PlannedProductionScheduleAssignment Schedule() => new()
    {
        Id = new PlannedProductionScheduleAssignmentId("POT"), CompanyId = new CompanyId("G"), SiteId = Site,
        TimeZoneId = new FactoryTimeZoneId("Asia/Kolkata"), EffectiveFrom = Day,
        PlannedWindows = [Window(7, 30, 16, 0), Window(16, 0, 0, 0), Window(0, 0, 7, 30, 1)],
        BreakWindows = [Window(12, 0, 12, 30), Window(20, 0, 20, 30), Window(3, 30, 4, 0, 1)],
    };

    private static ShiftScheduleAssignment[] Shifts() => Schedule().PlannedWindows.Select((window, index) => new ShiftScheduleAssignment
    {
        Id = new ShiftScheduleAssignmentId($"S{index}"), CompanyId = new CompanyId("G"), SiteId = Site,
        TimeZoneId = new FactoryTimeZoneId("Asia/Kolkata"), ShiftId = new ShiftId($"SHIFT-{index + 1}"), Name = $"Shift {index + 1}",
        EffectiveFrom = Day, StartsAtLocal = window.StartsAtLocal, EndsAtLocal = window.EndsAtLocal, StartDayOffset = window.StartDayOffset,
    }).ToArray();
}
