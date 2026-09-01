using FactoryConnect.Abstractions;
using FactoryConnect.Core;

namespace FactoryConnect.Core.Tests;

public sealed class InMemoryMachineShiftOccurrenceRosterStoreTests
{
    [Fact]
    public async Task MissingCoverageAndResolvedEmptyCoverageRemainDistinct()
    {
        var fixture = CreateFixture();

        Assert.Null(await fixture.Store.ReadAsync(
            fixture.MachineId,
            fixture.DayOne,
            CancellationToken.None));

        var empty = Roster(fixture, fixture.DayOne, 1, []);
        await fixture.Store.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(null, empty),
            CancellationToken.None);

        var stored = await fixture.Store.ReadAsync(
            fixture.MachineId,
            fixture.DayOne,
            CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Empty(stored.Occurrences);
    }

    [Fact]
    public async Task CompleteSnapshotCanBeAtomicallyReplacedAtExpectedRevision()
    {
        var fixture = CreateFixture();
        var first = Roster(fixture, fixture.DayOne, 1, [
            Ownership(fixture, fixture.DayOne, "SHIFT-A", 6),
        ]);
        await fixture.Store.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(null, first),
            CancellationToken.None);
        var replacement = Roster(fixture, fixture.DayOne, 2, [
            Ownership(fixture, fixture.DayOne, "SHIFT-B", 14),
        ]);

        await fixture.Store.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(first.Revision, replacement),
            CancellationToken.None);

        var stored = await fixture.Store.ReadAsync(
            fixture.MachineId,
            fixture.DayOne,
            CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(replacement.Revision, stored.Revision);
        Assert.Equal("SHIFT-B", Assert.Single(stored.Occurrences).ShiftOccurrenceId.ShiftId.Value);
    }

    [Fact]
    public async Task StaleRevisionRejectsReplacementWithoutMutation()
    {
        var fixture = CreateFixture();
        var first = Roster(fixture, fixture.DayOne, 1, []);
        await fixture.Store.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(null, first),
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Store.CommitAsync(
                new MachineShiftOccurrenceRosterCommit(
                    new MachineShiftOccurrenceRosterRevision(2),
                    Roster(fixture, fixture.DayOne, 3, [
                        Ownership(fixture, fixture.DayOne, "SHIFT-A", 6),
                    ])),
                CancellationToken.None));

        Assert.Same(first, await fixture.Store.ReadAsync(
            fixture.MachineId,
            fixture.DayOne,
            CancellationToken.None));
    }

    [Fact]
    public async Task SameOccurrenceMayApplyToMultipleMachinesOnSameProductionDay()
    {
        var fixture = CreateFixture();
        var occurrence = Shift(fixture.SiteId, "SHIFT-A", 6);
        var first = Roster(fixture, fixture.DayOne, 1, [
            new MachineShiftOccurrenceOwnership(
                fixture.MachineId,
                fixture.LineId,
                occurrence,
                fixture.DayOne),
        ]);
        await fixture.Store.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(null, first),
            CancellationToken.None);
        var otherMachine = new MachineId(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var second = new MachineShiftOccurrenceRoster(
            otherMachine,
            fixture.LineId,
            fixture.DayOne,
            new MachineShiftOccurrenceRosterRevision(1),
            [new MachineShiftOccurrenceOwnership(
                otherMachine,
                fixture.LineId,
                occurrence,
                fixture.DayOne)]);

        await fixture.Store.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(null, second),
            CancellationToken.None);

        Assert.NotNull(await fixture.Store.ReadAsync(
            otherMachine,
            fixture.DayOne,
            CancellationToken.None));
    }

    [Fact]
    public async Task ConflictingProductionDayOwnershipRejectsEntireCommit()
    {
        var fixture = CreateFixture();
        var occurrence = Shift(fixture.SiteId, "SHIFT-A", 22);
        var first = Roster(fixture, fixture.DayOne, 1, [
            new MachineShiftOccurrenceOwnership(
                fixture.MachineId,
                fixture.LineId,
                occurrence,
                fixture.DayOne),
        ]);
        await fixture.Store.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(null, first),
            CancellationToken.None);
        var conflicting = Roster(fixture, fixture.DayTwo, 1, [
            new MachineShiftOccurrenceOwnership(
                fixture.MachineId,
                fixture.LineId,
                occurrence,
                fixture.DayTwo),
        ]);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Store.CommitAsync(
                new MachineShiftOccurrenceRosterCommit(null, conflicting),
                CancellationToken.None));

        Assert.Null(await fixture.Store.ReadAsync(
            fixture.MachineId,
            fixture.DayTwo,
            CancellationToken.None));
        Assert.Same(first, await fixture.Store.ReadAsync(
            fixture.MachineId,
            fixture.DayOne,
            CancellationToken.None));
    }

    [Fact]
    public async Task CancellationPreventsReadAndCommit()
    {
        var fixture = CreateFixture();
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await fixture.Store.ReadAsync(
                fixture.MachineId,
                fixture.DayOne,
                source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await fixture.Store.CommitAsync(
                new MachineShiftOccurrenceRosterCommit(
                    null,
                    Roster(fixture, fixture.DayOne, 1, [])),
                source.Token));
    }

    private static MachineShiftOccurrenceOwnership Ownership(
        Fixture fixture,
        ProductionDayId day,
        string shiftId,
        int startsAtHour) =>
        new(
            fixture.MachineId,
            fixture.LineId,
            Shift(fixture.SiteId, shiftId, startsAtHour),
            day);

    private static ShiftOccurrenceId Shift(
        SiteId siteId,
        string shiftId,
        int startsAtHour)
    {
        var start = new DateTimeOffset(2026, 9, 1, startsAtHour, 0, 0, TimeSpan.Zero);
        return new ShiftOccurrenceId(
            siteId,
            new ShiftScheduleAssignmentId($"ASSIGNMENT-{shiftId}"),
            new ShiftId(shiftId),
            start,
            start.AddHours(8));
    }

    private static MachineShiftOccurrenceRoster Roster(
        Fixture fixture,
        ProductionDayId day,
        ulong revision,
        IEnumerable<MachineShiftOccurrenceOwnership> occurrences) =>
        new(
            fixture.MachineId,
            fixture.LineId,
            day,
            new MachineShiftOccurrenceRosterRevision(revision),
            occurrences);

    private static Fixture CreateFixture()
    {
        var siteId = new SiteId("SITE-A");
        return new Fixture(
            new InMemoryMachineShiftOccurrenceRosterStore(),
            new MachineId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            siteId,
            new ProductionLineId("LINE-1"),
            new ProductionDayId(siteId, new DateOnly(2026, 9, 1)),
            new ProductionDayId(siteId, new DateOnly(2026, 9, 2)));
    }

    private sealed record Fixture(
        InMemoryMachineShiftOccurrenceRosterStore Store,
        MachineId MachineId,
        SiteId SiteId,
        ProductionLineId LineId,
        ProductionDayId DayOne,
        ProductionDayId DayTwo);
}
