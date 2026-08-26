using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using Xunit;

namespace FactoryConnect.Core.Tests;

public sealed class ShiftScheduleHardeningTests
{
    [Fact]
    public void ReaderRejectsOverlappingDifferentShiftIdsInSameScope()
    {
        var siteId = new SiteId("SITE-1");
        var date = new DateOnly(2026, 8, 26);
        var reader = new InMemoryShiftScheduleReader([
            CreateAssignment("A1", siteId, null, "SHIFT-1", new TimeOnly(6, 0), new TimeOnly(14, 0), date),
        ]);

        Assert.Throws<InvalidOperationException>(() =>
            reader.AddAssignment(
                CreateAssignment("A2", siteId, null, "SHIFT-2", new TimeOnly(12, 0), new TimeOnly(20, 0), date)));
    }

    [Fact]
    public void ReaderAllowsAdjacentShiftsInSameScope()
    {
        var siteId = new SiteId("SITE-1");
        var date = new DateOnly(2026, 8, 26);
        var reader = new InMemoryShiftScheduleReader([
            CreateAssignment("A1", siteId, null, "SHIFT-1", new TimeOnly(6, 0), new TimeOnly(14, 0), date),
        ]);

        reader.AddAssignment(
            CreateAssignment("A2", siteId, null, "SHIFT-2", new TimeOnly(14, 0), new TimeOnly(22, 0), date));
    }

    [Fact]
    public void ReaderAllowsIdenticalShiftTimesOnDifferentLines()
    {
        var siteId = new SiteId("SITE-1");
        var date = new DateOnly(2026, 8, 26);
        var reader = new InMemoryShiftScheduleReader([
            CreateAssignment("A1", siteId, new ProductionLineId("LINE-1"), "SHIFT-1", new TimeOnly(6, 0), new TimeOnly(14, 0), date),
        ]);

        reader.AddAssignment(
            CreateAssignment("A2", siteId, new ProductionLineId("LINE-2"), "SHIFT-1", new TimeOnly(6, 0), new TimeOnly(14, 0), date));
    }

    [Fact]
    public void ReaderRejectsOverlapAgainstOvernightShift()
    {
        var siteId = new SiteId("SITE-1");
        var monday = new DateOnly(2026, 8, 24);
        var reader = new InMemoryShiftScheduleReader([
            CreateAssignment("NIGHT", siteId, null, "NIGHT", new TimeOnly(22, 0), new TimeOnly(6, 0), monday),
        ]);

        Assert.Throws<InvalidOperationException>(() =>
            reader.AddAssignment(
                CreateAssignment("EARLY", siteId, null, "EARLY", new TimeOnly(5, 0), new TimeOnly(13, 0), monday)));
    }

    [Fact]
    public void ReaderRejectsFollowingDayOverlapFromOvernightShift()
    {
        var siteId = new SiteId("SITE-1");
        var monday = new DateOnly(2026, 8, 24);
        var night = CreateAssignment(
            "NIGHT",
            siteId,
            null,
            "NIGHT",
            new TimeOnly(22, 0),
            new TimeOnly(6, 0),
            monday) with
        {
            ActiveDays = new HashSet<DayOfWeek> { DayOfWeek.Monday },
        };
        var tuesday = CreateAssignment(
            "TUESDAY",
            siteId,
            null,
            "DAY",
            new TimeOnly(5, 0),
            new TimeOnly(13, 0),
            monday) with
        {
            ActiveDays = new HashSet<DayOfWeek> { DayOfWeek.Tuesday },
        };
        var reader = new InMemoryShiftScheduleReader([night]);

        Assert.Throws<InvalidOperationException>(() => reader.AddAssignment(tuesday));
    }

    [Fact]
    public async Task ResolverUsesLineScheduleInsteadOfSiteSchedule()
    {
        var siteId = new SiteId("SITE-1");
        var lineId = new ProductionLineId("LINE-1");
        var date = new DateOnly(2026, 8, 26);
        var siteAssignment = CreateAssignment(
            "SITE",
            siteId,
            null,
            "SITE-SHIFT",
            new TimeOnly(6, 0),
            new TimeOnly(14, 0),
            date);
        var lineAssignment = CreateAssignment(
            "LINE",
            siteId,
            lineId,
            "LINE-SHIFT",
            new TimeOnly(7, 0),
            new TimeOnly(15, 0),
            date);
        var resolver = new ShiftOccurrenceResolver(
            new InMemoryShiftScheduleReader([siteAssignment, lineAssignment]));

        var result = await resolver.ResolveAsync(
            siteId,
            lineId,
            date,
            date.AddDays(1),
            CancellationToken.None);

        var occurrence = Assert.Single(result);
        Assert.Equal(lineAssignment.Id, occurrence.SourceAssignmentId);
        Assert.Equal(lineId, occurrence.ProductionLineId);
    }

    [Fact]
    public async Task ResolverFallsBackToSiteScheduleWhenLineHasNoSchedule()
    {
        var siteId = new SiteId("SITE-1");
        var lineId = new ProductionLineId("LINE-2");
        var date = new DateOnly(2026, 8, 26);
        var siteAssignment = CreateAssignment(
            "SITE",
            siteId,
            null,
            "SITE-SHIFT",
            new TimeOnly(6, 0),
            new TimeOnly(14, 0),
            date);
        var resolver = new ShiftOccurrenceResolver(
            new InMemoryShiftScheduleReader([siteAssignment]));

        var result = await resolver.ResolveAsync(
            siteId,
            lineId,
            date,
            date.AddDays(1),
            CancellationToken.None);

        var occurrence = Assert.Single(result);
        Assert.Equal(siteAssignment.Id, occurrence.SourceAssignmentId);
        Assert.Null(occurrence.ProductionLineId);
    }

    [Fact]
    public async Task ResolverHonorsActiveDays()
    {
        var siteId = new SiteId("SITE-1");
        var monday = new DateOnly(2026, 8, 24);
        var assignment = CreateAssignment(
            "A1",
            siteId,
            null,
            "SHIFT-1",
            new TimeOnly(6, 0),
            new TimeOnly(14, 0),
            monday) with
        {
            ActiveDays = new HashSet<DayOfWeek> { DayOfWeek.Monday },
        };
        var resolver = new ShiftOccurrenceResolver(
            new InMemoryShiftScheduleReader([assignment]));

        var result = await resolver.ResolveAsync(
            siteId,
            monday,
            monday.AddDays(2),
            CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(monday, result[0].FactoryDate);
    }

    [Fact]
    public async Task ResolverHonorsShiftSpecificShutdown()
    {
        var siteId = new SiteId("SITE-1");
        var date = new DateOnly(2026, 8, 26);
        var first = CreateAssignment("A1", siteId, null, "SHIFT-1", new TimeOnly(6, 0), new TimeOnly(14, 0), date);
        var second = CreateAssignment("A2", siteId, null, "SHIFT-2", new TimeOnly(14, 0), new TimeOnly(22, 0), date);
        var calendarOverride = new ShiftCalendarOverride
        {
            SiteId = siteId,
            FactoryDate = date,
            ShiftId = first.ShiftId,
            IsShutdown = true,
        };
        var resolver = new ShiftOccurrenceResolver(
            new InMemoryShiftScheduleReader([first, second], [calendarOverride]));

        var result = await resolver.ResolveAsync(
            siteId,
            date,
            date.AddDays(1),
            CancellationToken.None);

        var occurrence = Assert.Single(result);
        Assert.Equal(second.ShiftId, occurrence.ShiftId);
    }

    [Fact]
    public async Task OvernightShutdownUsesFactoryStartDate()
    {
        var siteId = new SiteId("SITE-1");
        var date = new DateOnly(2026, 8, 26);
        var night = CreateAssignment("NIGHT", siteId, null, "NIGHT", new TimeOnly(22, 0), new TimeOnly(6, 0), date);
        var calendarOverride = new ShiftCalendarOverride
        {
            SiteId = siteId,
            FactoryDate = date,
            ShiftId = night.ShiftId,
            IsShutdown = true,
        };
        var resolver = new ShiftOccurrenceResolver(
            new InMemoryShiftScheduleReader([night], [calendarOverride]));

        var result = await resolver.ResolveAsync(
            siteId,
            date,
            date.AddDays(1),
            CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public void AssignmentRejectsEmptyOptionalProductionLineId()
    {
        var assignment = CreateAssignment(
            "A1",
            new SiteId("SITE-1"),
            new ProductionLineId(" "),
            "SHIFT-1",
            new TimeOnly(6, 0),
            new TimeOnly(14, 0),
            new DateOnly(2026, 8, 26));

        Assert.Throws<ArgumentException>(assignment.Validate);
    }

    [Fact]
    public void AssignmentRejectsEmptyActiveDays()
    {
        var assignment = CreateAssignment(
            "A1",
            new SiteId("SITE-1"),
            null,
            "SHIFT-1",
            new TimeOnly(6, 0),
            new TimeOnly(14, 0),
            new DateOnly(2026, 8, 26)) with
        {
            ActiveDays = new HashSet<DayOfWeek>(),
        };

        Assert.Throws<ArgumentException>(assignment.Validate);
    }

    [Fact]
    public async Task ResolverRejectsInvalidProviderAssignment()
    {
        var siteId = new SiteId("SITE-1");
        var date = new DateOnly(2026, 8, 26);
        var invalid = CreateAssignment(
            "A1",
            siteId,
            null,
            "SHIFT-1",
            new TimeOnly(6, 0),
            new TimeOnly(6, 0),
            date);
        var resolver = new ShiftOccurrenceResolver(
            new StubShiftScheduleReader([invalid], []));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            resolver.ResolveAsync(siteId, date, date.AddDays(1), CancellationToken.None));
    }

    [Fact]
    public async Task ResolverRejectsInvalidProviderOverride()
    {
        var siteId = new SiteId("SITE-1");
        var date = new DateOnly(2026, 8, 26);
        var assignment = CreateAssignment(
            "A1",
            siteId,
            null,
            "SHIFT-1",
            new TimeOnly(6, 0),
            new TimeOnly(14, 0),
            date);
        var invalidOverride = new ShiftCalendarOverride
        {
            SiteId = default,
            FactoryDate = date,
            IsShutdown = true,
        };
        var resolver = new ShiftOccurrenceResolver(
            new StubShiftScheduleReader([assignment], [invalidOverride]));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            resolver.ResolveAsync(siteId, date, date.AddDays(1), CancellationToken.None));
    }

    [Fact]
    public async Task ResolverPropagatesFailureFromAssignmentRead()
    {
        var siteId = new SiteId("SITE-1");
        var date = new DateOnly(2026, 8, 26);
        var resolver = new ShiftOccurrenceResolver(
            new ThrowingShiftScheduleReader(throwOnAssignments: true));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync(siteId, date, date.AddDays(1), CancellationToken.None));
    }

    [Fact]
    public async Task ResolverPropagatesFailureFromOverrideRead()
    {
        var siteId = new SiteId("SITE-1");
        var date = new DateOnly(2026, 8, 26);
        var resolver = new ShiftOccurrenceResolver(
            new ThrowingShiftScheduleReader(throwOnAssignments: false));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync(siteId, date, date.AddDays(1), CancellationToken.None));
    }

    [Fact]
    public async Task ResolverAdvancesInvalidSpringForwardBoundary()
    {
        var siteId = new SiteId("SITE-1");
        var date = new DateOnly(2026, 3, 8);
        var assignment = CreateAssignment(
            "DST",
            siteId,
            null,
            "DST",
            new TimeOnly(2, 30),
            new TimeOnly(4, 30),
            date) with
        {
            TimeZoneId = new FactoryTimeZoneId("America/New_York"),
        };
        var resolver = new ShiftOccurrenceResolver(
            new InMemoryShiftScheduleReader([assignment]));

        var result = await resolver.ResolveAsync(
            siteId,
            date,
            date.AddDays(1),
            CancellationToken.None);

        var occurrence = Assert.Single(result);
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero), occurrence.StartsAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 8, 30, 0, TimeSpan.Zero), occurrence.EndsAtUtc);
    }

    [Fact]
    public async Task ResolverUsesEarlierUtcForAmbiguousStart()
    {
        var siteId = new SiteId("SITE-1");
        var date = new DateOnly(2026, 11, 1);
        var assignment = CreateAssignment(
            "DST",
            siteId,
            null,
            "DST",
            new TimeOnly(1, 30),
            new TimeOnly(2, 30),
            date) with
        {
            TimeZoneId = new FactoryTimeZoneId("America/New_York"),
        };
        var resolver = new ShiftOccurrenceResolver(
            new InMemoryShiftScheduleReader([assignment]));

        var result = await resolver.ResolveAsync(
            siteId,
            date,
            date.AddDays(1),
            CancellationToken.None);

        var occurrence = Assert.Single(result);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), occurrence.StartsAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 7, 30, 0, TimeSpan.Zero), occurrence.EndsAtUtc);
    }

    [Fact]
    public async Task ResolverRejectsUnknownTimeZoneId()
    {
        var siteId = new SiteId("SITE-1");
        var date = new DateOnly(2026, 8, 26);
        var assignment = CreateAssignment(
            "A1",
            siteId,
            null,
            "SHIFT-1",
            new TimeOnly(6, 0),
            new TimeOnly(14, 0),
            date) with
        {
            TimeZoneId = new FactoryTimeZoneId("FactoryConnect/Unknown-Time-Zone"),
        };
        var resolver = new ShiftOccurrenceResolver(
            new InMemoryShiftScheduleReader([assignment]));

        await Assert.ThrowsAsync<TimeZoneNotFoundException>(() =>
            resolver.ResolveAsync(siteId, date, date.AddDays(1), CancellationToken.None));
    }

    private static ShiftScheduleAssignment CreateAssignment(
        string assignmentId,
        SiteId siteId,
        ProductionLineId? productionLineId,
        string shiftId,
        TimeOnly startsAt,
        TimeOnly endsAt,
        DateOnly effectiveFrom) =>
        new()
        {
            Id = new ShiftScheduleAssignmentId(assignmentId),
            CompanyId = new CompanyId("COMP-1"),
            SiteId = siteId,
            ProductionLineId = productionLineId,
            TimeZoneId = new FactoryTimeZoneId("Asia/Kolkata"),
            ShiftId = new ShiftId(shiftId),
            Name = shiftId,
            StartsAtLocal = startsAt,
            EndsAtLocal = endsAt,
            EffectiveFrom = effectiveFrom,
        };

    private sealed class StubShiftScheduleReader : IShiftScheduleReader
    {
        private readonly IReadOnlyList<ShiftScheduleAssignment> _assignments;
        private readonly IReadOnlyList<ShiftCalendarOverride> _overrides;

        public StubShiftScheduleReader(
            IReadOnlyList<ShiftScheduleAssignment> assignments,
            IReadOnlyList<ShiftCalendarOverride> overrides)
        {
            _assignments = assignments;
            _overrides = overrides;
        }

        public Task<IReadOnlyList<ShiftScheduleAssignment>> ReadAssignmentsAsync(
            SiteId siteId,
            DateOnly factoryDateFrom,
            DateOnly factoryDateTo,
            CancellationToken cancellationToken) =>
            Task.FromResult(_assignments);

        public Task<IReadOnlyList<ShiftCalendarOverride>> ReadExceptionsAsync(
            SiteId siteId,
            DateOnly factoryDateFrom,
            DateOnly factoryDateTo,
            CancellationToken cancellationToken) =>
            Task.FromResult(_overrides);
    }

    private sealed class ThrowingShiftScheduleReader : IShiftScheduleReader
    {
        private readonly bool _throwOnAssignments;

        public ThrowingShiftScheduleReader(bool throwOnAssignments)
        {
            _throwOnAssignments = throwOnAssignments;
        }

        public Task<IReadOnlyList<ShiftScheduleAssignment>> ReadAssignmentsAsync(
            SiteId siteId,
            DateOnly factoryDateFrom,
            DateOnly factoryDateTo,
            CancellationToken cancellationToken)
        {
            if (_throwOnAssignments)
            {
                throw new InvalidOperationException("Assignment read failed.");
            }

            return Task.FromResult<IReadOnlyList<ShiftScheduleAssignment>>([]);
        }

        public Task<IReadOnlyList<ShiftCalendarOverride>> ReadExceptionsAsync(
            SiteId siteId,
            DateOnly factoryDateFrom,
            DateOnly factoryDateTo,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Override read failed.");
        }
    }
}
