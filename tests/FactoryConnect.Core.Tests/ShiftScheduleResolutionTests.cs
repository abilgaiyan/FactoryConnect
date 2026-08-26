using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using Xunit;

namespace FactoryConnect.Core.Tests;

public sealed class ShiftScheduleResolutionTests
{
    [Fact]
    public async Task ResolverMaterializesThreeEightHourShiftsInUtc()
    {
        var siteId = new SiteId("SITE-1");
        var date = new DateOnly(2026, 8, 26);
        var reader = new InMemoryShiftScheduleReader([
            CreateAssignment("S1-A", siteId, "SHIFT-1", new TimeOnly(6, 0), new TimeOnly(14, 0), date),
            CreateAssignment("S2-A", siteId, "SHIFT-2", new TimeOnly(14, 0), new TimeOnly(22, 0), date),
            CreateAssignment("S3-A", siteId, "SHIFT-3", new TimeOnly(22, 0), new TimeOnly(6, 0), date),
        ]);
        var resolver = new ShiftOccurrenceResolver(reader);

        var result = await resolver.ResolveAsync(siteId, date, date.AddDays(1), CancellationToken.None);

        Assert.Equal(3, result.Count);
        Assert.Equal(new DateTimeOffset(2026, 8, 26, 0, 30, 0, TimeSpan.Zero), result[0].StartsAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 8, 26, 8, 30, 0, TimeSpan.Zero), result[0].EndsAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 8, 26, 16, 30, 0, TimeSpan.Zero), result[2].StartsAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 8, 27, 0, 30, 0, TimeSpan.Zero), result[2].EndsAtUtc);
    }

    [Fact]
    public async Task ResolverSupportsAnyConfiguredShiftCount()
    {
        var siteId = new SiteId("SITE-1");
        var date = new DateOnly(2026, 8, 26);
        var reader = new InMemoryShiftScheduleReader([
            CreateAssignment("DAY", siteId, "DAY", new TimeOnly(6, 0), new TimeOnly(18, 0), date),
            CreateAssignment("NIGHT", siteId, "NIGHT", new TimeOnly(18, 0), new TimeOnly(6, 0), date),
        ]);
        var resolver = new ShiftOccurrenceResolver(reader);

        var result = await resolver.ResolveAsync(siteId, date, date.AddDays(1), CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.All(result, occurrence => Assert.Equal(TimeSpan.FromHours(12), occurrence.Duration));
    }

    [Fact]
    public async Task ResolverHonorsShutdownDay()
    {
        var siteId = new SiteId("SITE-1");
        var date = new DateOnly(2026, 8, 26);
        var assignment = CreateAssignment("A1", siteId, "SHIFT-1", new TimeOnly(6, 0), new TimeOnly(14, 0), date);
        var reader = new InMemoryShiftScheduleReader(
            [assignment],
            [new ShiftCalendarException { SiteId = siteId, FactoryDate = date, IsShutdown = true }]);
        var resolver = new ShiftOccurrenceResolver(reader);

        var result = await resolver.ResolveAsync(siteId, date, date.AddDays(1), CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task ResolverPreservesHistoricalScheduleAfterLaterChange()
    {
        var siteId = new SiteId("SITE-1");
        var oldDate = new DateOnly(2026, 8, 26);
        var changeDate = oldDate.AddDays(1);
        var oldAssignment = CreateAssignment("OLD", siteId, "SHIFT-1", new TimeOnly(6, 0), new TimeOnly(14, 0), oldDate) with
        {
            EffectiveTo = changeDate,
        };
        var newAssignment = CreateAssignment("NEW", siteId, "SHIFT-1", new TimeOnly(7, 0), new TimeOnly(15, 0), changeDate);
        var resolver = new ShiftOccurrenceResolver(new InMemoryShiftScheduleReader([oldAssignment, newAssignment]));

        var result = await resolver.ResolveAsync(siteId, oldDate, changeDate.AddDays(1), CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal(oldAssignment.Id, result[0].SourceAssignmentId);
        Assert.Equal(newAssignment.Id, result[1].SourceAssignmentId);
    }

    [Fact]
    public void ReaderRejectsOverlappingEffectiveSchedulesForSameShiftScope()
    {
        var siteId = new SiteId("SITE-1");
        var date = new DateOnly(2026, 8, 26);
        var reader = new InMemoryShiftScheduleReader([
            CreateAssignment("A1", siteId, "SHIFT-1", new TimeOnly(6, 0), new TimeOnly(14, 0), date),
        ]);

        Assert.Throws<InvalidOperationException>(() =>
            reader.AddAssignment(CreateAssignment("A2", siteId, "SHIFT-1", new TimeOnly(7, 0), new TimeOnly(15, 0), date.AddDays(1))));
    }

    [Fact]
    public async Task ReaderPropagatesCancellation()
    {
        var reader = new InMemoryShiftScheduleReader();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var date = new DateOnly(2026, 8, 26);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            reader.ReadAssignmentsAsync(
                new SiteId("SITE-1"),
                date,
                date.AddDays(1),
                cancellation.Token));
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
}
