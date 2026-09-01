using FactoryConnect.Abstractions;
using FactoryConnect.Core;

namespace FactoryConnect.Core.Tests;

public sealed class MachineShiftOccurrenceRosterMaterializerTests
{
    [Fact]
    public async Task MaterializesLineApplicableOvernightOccurrenceAndPersistsCompleteRoster()
    {
        var fixture = CreateFixture([
            Assignment("SITE-DAY", "SHIFT-DAY", null, new TimeOnly(6, 0), new TimeOnly(14, 0)),
            Assignment("LINE-NIGHT", "SHIFT-NIGHT", LineId, new TimeOnly(22, 0), new TimeOnly(6, 0)),
        ]);

        var roster = await fixture.Materializer.MaterializeAsync(
            fixture.Scope,
            fixture.ProductionDayId,
            CancellationToken.None);

        var ownership = Assert.Single(roster.Occurrences);
        Assert.Equal("LINE-NIGHT", ownership.ShiftOccurrenceId.ShiftScheduleAssignmentId.Value);
        Assert.Equal(fixture.MachineId, ownership.MachineId);
        Assert.Equal(fixture.LineId, ownership.ProductionLineId);
        Assert.Equal(fixture.ProductionDayId, ownership.ProductionDayId);
        Assert.Equal(TimeSpan.FromHours(8),
            ownership.ShiftOccurrenceId.EndsAtUtc - ownership.ShiftOccurrenceId.StartsAtUtc);
        Assert.Same(roster, await fixture.Store.ReadAsync(
            fixture.MachineId,
            fixture.ProductionDayId,
            CancellationToken.None));
    }

    [Fact]
    public async Task ShutdownDayPublishesResolvedEmptyCoverage()
    {
        var shutdown = new ShiftCalendarOverride
        {
            SiteId = SiteId,
            FactoryDate = BusinessDate,
            IsShutdown = true,
        };
        var fixture = CreateFixture([
            Assignment("LINE-DAY", "SHIFT-DAY", LineId, new TimeOnly(6, 0), new TimeOnly(14, 0)),
        ], [shutdown]);

        var roster = await fixture.Materializer.MaterializeAsync(
            fixture.Scope,
            fixture.ProductionDayId,
            CancellationToken.None);

        Assert.Empty(roster.Occurrences);
        Assert.NotNull(await fixture.Store.ReadAsync(
            fixture.MachineId,
            fixture.ProductionDayId,
            CancellationToken.None));
        Assert.Null(await fixture.Store.ReadAsync(
            fixture.MachineId,
            new ProductionDayId(SiteId, BusinessDate.AddDays(1)),
            CancellationToken.None));
    }

    [Fact]
    public async Task EquivalentResolutionIsIdempotentAndDoesNotAdvanceRevision()
    {
        var fixture = CreateFixture([
            Assignment("LINE-DAY", "SHIFT-DAY", LineId, new TimeOnly(6, 0), new TimeOnly(14, 0)),
        ]);
        var first = await fixture.Materializer.MaterializeAsync(
            fixture.Scope,
            fixture.ProductionDayId,
            CancellationToken.None);

        var replay = await fixture.Materializer.MaterializeAsync(
            fixture.Scope,
            fixture.ProductionDayId,
            CancellationToken.None);

        Assert.Same(first, replay);
        Assert.Equal(new MachineShiftOccurrenceRosterRevision(1), replay.Revision);
    }

    [Fact]
    public async Task ChangedAuthoritativeResolutionReplacesCompleteSnapshotAtNextRevision()
    {
        var fixture = CreateFixture([
            Assignment("LINE-DAY", "SHIFT-DAY", LineId, new TimeOnly(6, 0), new TimeOnly(14, 0)),
        ]);
        var first = await fixture.Materializer.MaterializeAsync(
            fixture.Scope,
            fixture.ProductionDayId,
            CancellationToken.None);
        fixture.Reader.AddException(new ShiftCalendarOverride
        {
            SiteId = SiteId,
            FactoryDate = BusinessDate,
            IsShutdown = true,
        });

        var replacement = await fixture.Materializer.MaterializeAsync(
            fixture.Scope,
            fixture.ProductionDayId,
            CancellationToken.None);

        Assert.Single(first.Occurrences);
        Assert.Empty(replacement.Occurrences);
        Assert.Equal(new MachineShiftOccurrenceRosterRevision(2), replacement.Revision);
    }

    [Fact]
    public async Task DstTransitionRemainsOwnedByFactoryDateWithoutUtcBoundaryInference()
    {
        var fixture = CreateFixture([
            Assignment(
                "DST-NIGHT",
                "SHIFT-NIGHT",
                LineId,
                new TimeOnly(0, 0),
                new TimeOnly(3, 0),
                "America/New_York"),
        ], businessDate: new DateOnly(2026, 11, 1));

        var roster = await fixture.Materializer.MaterializeAsync(
            fixture.Scope,
            fixture.ProductionDayId,
            CancellationToken.None);

        var occurrence = Assert.Single(roster.Occurrences).ShiftOccurrenceId;
        Assert.Equal(TimeSpan.FromHours(4), occurrence.EndsAtUtc - occurrence.StartsAtUtc);
        Assert.Equal(fixture.ProductionDayId, Assert.Single(roster.Occurrences).ProductionDayId);
    }

    [Fact]
    public async Task DifferentSiteProductionDayIsRejectedBeforeResolutionOrPersistence()
    {
        var fixture = CreateFixture([]);
        var otherDay = new ProductionDayId(new SiteId("SITE-B"), BusinessDate);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await fixture.Materializer.MaterializeAsync(
                fixture.Scope,
                otherDay,
                CancellationToken.None));

        Assert.Null(await fixture.Store.ReadAsync(
            fixture.MachineId,
            otherDay,
            CancellationToken.None));
    }

    [Fact]
    public async Task RuntimeSetRoutesOnlyConfiguredMachineScope()
    {
        var fixture = CreateFixture([]);
        var runtimes = new MachineShiftOccurrenceRosterMaterializationRuntimeSet(
            [fixture.Scope],
            fixture.Materializer);

        var roster = await runtimes.MaterializeAsync(
            fixture.MachineId,
            fixture.ProductionDayId,
            CancellationToken.None);

        Assert.Empty(roster.Occurrences);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await runtimes.MaterializeAsync(
                new MachineId(Guid.Parse("22222222-2222-2222-2222-222222222222")),
                fixture.ProductionDayId,
                CancellationToken.None));
    }

    private static readonly SiteId SiteId = new("SITE-A");
    private static readonly ProductionLineId LineId = new("LINE-1");
    private static readonly DateOnly BusinessDate = new(2026, 9, 1);

    private static Fixture CreateFixture(
        IEnumerable<ShiftScheduleAssignment> assignments,
        IEnumerable<ShiftCalendarOverride>? exceptions = null,
        DateOnly? businessDate = null)
    {
        var machineId = new MachineId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var reader = new InMemoryShiftScheduleReader(assignments, exceptions);
        var store = new InMemoryMachineShiftOccurrenceRosterStore();
        var day = new ProductionDayId(SiteId, businessDate ?? BusinessDate);
        return new Fixture(
            machineId,
            LineId,
            new MachineShiftScheduleScope(machineId, SiteId, LineId),
            day,
            reader,
            store,
            new MachineShiftOccurrenceRosterMaterializer(
                new ShiftOccurrenceResolver(reader),
                store));
    }

    private static ShiftScheduleAssignment Assignment(
        string assignmentId,
        string shiftId,
        ProductionLineId? lineId,
        TimeOnly startsAt,
        TimeOnly endsAt,
        string timeZoneId = "Asia/Kolkata") =>
        new()
        {
            Id = new ShiftScheduleAssignmentId(assignmentId),
            CompanyId = new CompanyId("COMPANY-A"),
            SiteId = SiteId,
            ProductionLineId = lineId,
            TimeZoneId = new FactoryTimeZoneId(timeZoneId),
            ShiftId = new ShiftId(shiftId),
            Name = shiftId,
            StartsAtLocal = startsAt,
            EndsAtLocal = endsAt,
            EffectiveFrom = new DateOnly(2026, 1, 1),
        };

    private sealed record Fixture(
        MachineId MachineId,
        ProductionLineId LineId,
        MachineShiftScheduleScope Scope,
        ProductionDayId ProductionDayId,
        InMemoryShiftScheduleReader Reader,
        InMemoryMachineShiftOccurrenceRosterStore Store,
        MachineShiftOccurrenceRosterMaterializer Materializer);
}
