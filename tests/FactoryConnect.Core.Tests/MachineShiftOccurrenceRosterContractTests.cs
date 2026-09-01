using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Tests;

public sealed class MachineShiftOccurrenceRosterContractTests
{
    [Fact]
    public void EmptyRosterIsValidResolvedCoverage()
    {
        var fixture = CreateFixture();

        var roster = new MachineShiftOccurrenceRoster(
            fixture.MachineId,
            fixture.LineId,
            fixture.ProductionDayId,
            new MachineShiftOccurrenceRosterRevision(1),
            []);

        Assert.Empty(roster.Occurrences);
    }

    [Fact]
    public void RosterOrdersOccurrencesByAuthoritativeOccurrenceIdentity()
    {
        var fixture = CreateFixture();
        var later = Ownership(fixture, "SHIFT-B", 14);
        var earlier = Ownership(fixture, "SHIFT-A", 6);

        var roster = Roster(fixture, 1, [later, earlier]);

        Assert.Equal(new[] { earlier, later }, roster.Occurrences);
    }

    [Fact]
    public void OwnershipRejectsDifferentShiftAndProductionDaySites()
    {
        var fixture = CreateFixture();
        var otherSiteDay = new ProductionDayId(
            new SiteId("SITE-B"),
            fixture.ProductionDayId.BusinessDate);

        Assert.Throws<ArgumentException>(() => new MachineShiftOccurrenceOwnership(
            fixture.MachineId,
            fixture.LineId,
            Shift(fixture, "SHIFT-A", 6),
            otherSiteDay));
    }

    [Fact]
    public void RosterRejectsOccurrenceForDifferentMachineLineOrDay()
    {
        var fixture = CreateFixture();
        var ownership = Ownership(fixture, "SHIFT-A", 6);

        Assert.Throws<ArgumentException>(() => new MachineShiftOccurrenceRoster(
            new MachineId(Guid.Parse("22222222-2222-2222-2222-222222222222")),
            fixture.LineId,
            fixture.ProductionDayId,
            new MachineShiftOccurrenceRosterRevision(1),
            [ownership]));
        Assert.Throws<ArgumentException>(() => new MachineShiftOccurrenceRoster(
            fixture.MachineId,
            new ProductionLineId("LINE-2"),
            fixture.ProductionDayId,
            new MachineShiftOccurrenceRosterRevision(1),
            [ownership]));
        Assert.Throws<ArgumentException>(() => new MachineShiftOccurrenceRoster(
            fixture.MachineId,
            fixture.LineId,
            new ProductionDayId(fixture.SiteId, new DateOnly(2026, 9, 2)),
            new MachineShiftOccurrenceRosterRevision(1),
            [ownership]));
    }

    [Fact]
    public void RosterRejectsDuplicateOccurrenceIdentity()
    {
        var fixture = CreateFixture();
        var ownership = Ownership(fixture, "SHIFT-A", 6);

        Assert.Throws<ArgumentException>(() => Roster(
            fixture,
            1,
            [ownership, ownership]));
    }

    private static MachineShiftOccurrenceOwnership Ownership(
        Fixture fixture,
        string shiftId,
        int startsAtHour) =>
        new(
            fixture.MachineId,
            fixture.LineId,
            Shift(fixture, shiftId, startsAtHour),
            fixture.ProductionDayId);

    private static ShiftOccurrenceId Shift(
        Fixture fixture,
        string shiftId,
        int startsAtHour)
    {
        var start = new DateTimeOffset(2026, 9, 1, startsAtHour, 0, 0, TimeSpan.Zero);
        return new ShiftOccurrenceId(
            fixture.SiteId,
            new ShiftScheduleAssignmentId($"ASSIGNMENT-{shiftId}"),
            new ShiftId(shiftId),
            start,
            start.AddHours(8));
    }

    private static MachineShiftOccurrenceRoster Roster(
        Fixture fixture,
        ulong revision,
        IEnumerable<MachineShiftOccurrenceOwnership> occurrences) =>
        new(
            fixture.MachineId,
            fixture.LineId,
            fixture.ProductionDayId,
            new MachineShiftOccurrenceRosterRevision(revision),
            occurrences);

    private static Fixture CreateFixture()
    {
        var siteId = new SiteId("SITE-A");
        return new Fixture(
            new MachineId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            siteId,
            new ProductionLineId("LINE-1"),
            new ProductionDayId(siteId, new DateOnly(2026, 9, 1)));
    }

    private sealed record Fixture(
        MachineId MachineId,
        SiteId SiteId,
        ProductionLineId LineId,
        ProductionDayId ProductionDayId);
}
