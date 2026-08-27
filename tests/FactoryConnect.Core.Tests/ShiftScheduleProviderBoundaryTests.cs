using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using Xunit;

namespace FactoryConnect.Core.Tests;

public sealed class ShiftScheduleProviderBoundaryTests
{
    [Fact]
    public async Task ResolverRejectsOverlappingProviderAssignments()
    {
        var siteId = new SiteId("SITE-1");
        var date = new DateOnly(2026, 8, 26);
        var first = CreateAssignment("A1", siteId, "SHIFT-1", new TimeOnly(6, 0), new TimeOnly(14, 0), date);
        var second = CreateAssignment("A2", siteId, "SHIFT-2", new TimeOnly(12, 0), new TimeOnly(20, 0), date);
        var resolver = new ShiftOccurrenceResolver(new StubReader([first, second], []));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync(siteId, date, date.AddDays(1), CancellationToken.None));
    }

    [Fact]
    public async Task ResolverRejectsDuplicateProviderAssignmentIds()
    {
        var siteId = new SiteId("SITE-1");
        var date = new DateOnly(2026, 8, 26);
        var first = CreateAssignment("A1", siteId, "SHIFT-1", new TimeOnly(6, 0), new TimeOnly(14, 0), date);
        var second = CreateAssignment("A1", siteId, "SHIFT-2", new TimeOnly(14, 0), new TimeOnly(22, 0), date);
        var resolver = new ShiftOccurrenceResolver(new StubReader([first, second], []));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync(siteId, date, date.AddDays(1), CancellationToken.None));
    }

    [Fact]
    public async Task ResolverRejectsProviderAssignmentForDifferentSite()
    {
        var requestedSite = new SiteId("SITE-1");
        var date = new DateOnly(2026, 8, 26);
        var assignment = CreateAssignment(
            "A1",
            new SiteId("SITE-2"),
            "SHIFT-1",
            new TimeOnly(6, 0),
            new TimeOnly(14, 0),
            date);
        var resolver = new ShiftOccurrenceResolver(new StubReader([assignment], []));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync(requestedSite, date, date.AddDays(1), CancellationToken.None));
    }

    [Fact]
    public async Task ResolverRejectsProviderOverrideForDifferentSite()
    {
        var requestedSite = new SiteId("SITE-1");
        var date = new DateOnly(2026, 8, 26);
        var assignment = CreateAssignment("A1", requestedSite, "SHIFT-1", new TimeOnly(6, 0), new TimeOnly(14, 0), date);
        var calendarOverride = new ShiftCalendarOverride
        {
            SiteId = new SiteId("SITE-2"),
            FactoryDate = date,
            IsShutdown = true,
        };
        var resolver = new ShiftOccurrenceResolver(new StubReader([assignment], [calendarOverride]));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync(requestedSite, date, date.AddDays(1), CancellationToken.None));
    }

    [Fact]
    public async Task ResolverRejectsProviderAssignmentOutsideRequestedInterval()
    {
        var siteId = new SiteId("SITE-1");
        var date = new DateOnly(2026, 8, 26);
        var assignment = CreateAssignment(
            "A1",
            siteId,
            "SHIFT-1",
            new TimeOnly(6, 0),
            new TimeOnly(14, 0),
            date.AddDays(-3)) with
        {
            EffectiveTo = date.AddDays(-1),
        };
        var resolver = new ShiftOccurrenceResolver(new StubReader([assignment], []));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync(siteId, date, date.AddDays(1), CancellationToken.None));
    }

    [Fact]
    public async Task ResolverRejectsProviderOverrideOutsideRequestedInterval()
    {
        var siteId = new SiteId("SITE-1");
        var date = new DateOnly(2026, 8, 26);
        var assignment = CreateAssignment("A1", siteId, "SHIFT-1", new TimeOnly(6, 0), new TimeOnly(14, 0), date);
        var calendarOverride = new ShiftCalendarOverride
        {
            SiteId = siteId,
            FactoryDate = date.AddDays(1),
            IsShutdown = true,
        };
        var resolver = new ShiftOccurrenceResolver(new StubReader([assignment], [calendarOverride]));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync(siteId, date, date.AddDays(1), CancellationToken.None));
    }

    private static ShiftScheduleAssignment CreateAssignment(
        string assignmentId,
        SiteId siteId,
        string shiftId,
        TimeOnly startsAt,
        TimeOnly endsAt,
        DateOnly effectiveFrom) =>
        new()
        {
            Id = new ShiftScheduleAssignmentId(assignmentId),
            CompanyId = new CompanyId("COMP-1"),
            SiteId = siteId,
            TimeZoneId = new FactoryTimeZoneId("Asia/Kolkata"),
            ShiftId = new ShiftId(shiftId),
            Name = shiftId,
            StartsAtLocal = startsAt,
            EndsAtLocal = endsAt,
            EffectiveFrom = effectiveFrom,
        };

    private sealed class StubReader : IShiftScheduleReader
    {
        private readonly IReadOnlyList<ShiftScheduleAssignment> _assignments;
        private readonly IReadOnlyList<ShiftCalendarOverride> _calendarOverrides;

        public StubReader(
            IReadOnlyList<ShiftScheduleAssignment> assignments,
            IReadOnlyList<ShiftCalendarOverride> calendarOverrides)
        {
            _assignments = assignments;
            _calendarOverrides = calendarOverrides;
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
            Task.FromResult(_calendarOverrides);
    }
}
