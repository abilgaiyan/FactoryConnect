using FactoryConnect.Abstractions;

namespace FactoryConnect.Integration.Tests;

public abstract class MachineShiftOccurrenceRosterStoreConformanceTests
{
    protected abstract IMachineShiftOccurrenceRosterStore CreateStore();

    [Fact]
    public async Task UnknownExactIdentityReturnsAbsent()
    {
        var store = CreateStore();

        var result = await store.ReadAsync(
            MachineId.New(),
            Day(Site(), 1),
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task InitialRosterRoundTripsExactIdentityRevisionAndCanonicalOrder()
    {
        var store = CreateStore();
        var machineId = MachineId.New();
        var day = Day(Site(), 2);
        var lineId = new ProductionLineId("LINE-E3-ROUNDTRIP");
        var starts = new DateTimeOffset(2026, 9, 2, 6, 0, 0, TimeSpan.Zero);
        var ends = starts.AddHours(2);
        var roster = Roster(
            machineId,
            lineId,
            day,
            1,
            Ownership(machineId, lineId, day, "B", "A", starts, ends),
            Ownership(machineId, lineId, day, "A", "B", starts, ends),
            Ownership(machineId, lineId, day, "A", "A", starts, ends),
            Ownership(machineId, lineId, day, "LATER", "SHIFT", starts.AddHours(4), ends.AddHours(4)));

        await store.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(null, roster),
            CancellationToken.None);

        var actual = Assert.IsType<MachineShiftOccurrenceRoster>(
            await store.ReadAsync(machineId, day, CancellationToken.None));
        Assert.Equal(machineId, actual.MachineId);
        Assert.Equal(lineId, actual.ProductionLineId);
        Assert.Equal(day, actual.ProductionDayId);
        Assert.Equal(1UL, actual.Revision.Value);
        Assert.Equal(
            ["A:A", "A:B", "B:A", "LATER:SHIFT"],
            actual.Occurrences
                .Select(static ownership =>
                    $"{ownership.ShiftOccurrenceId.ShiftScheduleAssignmentId.Value}:{ownership.ShiftOccurrenceId.ShiftId.Value}")
                .ToArray());
    }

    [Fact]
    public async Task EmptyRosterRoundTripsAndCanBeReplacedByNextEmptyRevision()
    {
        var store = CreateStore();
        var machineId = MachineId.New();
        var day = Day(Site(), 3);
        var lineId = new ProductionLineId("LINE-E3-EMPTY");

        await store.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(
                null,
                Roster(machineId, lineId, day, 1)),
            CancellationToken.None);
        await store.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(
                new MachineShiftOccurrenceRosterRevision(1),
                Roster(machineId, lineId, day, 2)),
            CancellationToken.None);

        var actual = Assert.IsType<MachineShiftOccurrenceRoster>(
            await store.ReadAsync(machineId, day, CancellationToken.None));
        Assert.Equal(2UL, actual.Revision.Value);
        Assert.Empty(actual.Occurrences);
    }

    [Fact]
    public async Task ReplacementCompletelyReplacesOccurrenceSnapshot()
    {
        var store = CreateStore();
        var machineId = MachineId.New();
        var day = Day(Site(), 4);
        var lineId = new ProductionLineId("LINE-E3-REPLACE");

        await store.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(
                null,
                Roster(
                    machineId,
                    lineId,
                    day,
                    1,
                    Ownership(machineId, lineId, day, "OLD", "OLD", 6, 8))),
            CancellationToken.None);
        await store.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(
                new MachineShiftOccurrenceRosterRevision(1),
                Roster(
                    machineId,
                    lineId,
                    day,
                    2,
                    Ownership(machineId, lineId, day, "NEW", "NEW", 10, 12))),
            CancellationToken.None);

        var actual = Assert.IsType<MachineShiftOccurrenceRoster>(
            await store.ReadAsync(machineId, day, CancellationToken.None));
        Assert.Equal(2UL, actual.Revision.Value);
        var occurrence = Assert.Single(actual.Occurrences);
        Assert.Equal("NEW", occurrence.ShiftOccurrenceId.ShiftId.Value);
    }

    [Fact]
    public async Task InitialRevisionMustBeOneWithoutChangingState()
    {
        var store = CreateStore();
        var machineId = MachineId.New();
        var day = Day(Site(), 5);
        var lineId = new ProductionLineId("LINE-E3-INITIAL-REV");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.CommitAsync(
                new MachineShiftOccurrenceRosterCommit(
                    null,
                    Roster(machineId, lineId, day, 2)),
                CancellationToken.None));

        Assert.Null(await store.ReadAsync(machineId, day, CancellationToken.None));
    }

    [Fact]
    public async Task StaleExpectedRevisionIsRejectedWithoutChangingState()
    {
        var store = CreateStore();
        var machineId = MachineId.New();
        var day = Day(Site(), 6);
        var lineId = new ProductionLineId("LINE-E3-STALE");
        await store.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(
                null,
                Roster(machineId, lineId, day, 1)),
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.CommitAsync(
                new MachineShiftOccurrenceRosterCommit(
                    null,
                    Roster(machineId, lineId, day, 2)),
                CancellationToken.None));

        var actual = Assert.IsType<MachineShiftOccurrenceRoster>(
            await store.ReadAsync(machineId, day, CancellationToken.None));
        Assert.Equal(1UL, actual.Revision.Value);
    }

    [Fact]
    public async Task ReplacementRevisionMustBeExactlyCurrentPlusOneWithoutChangingState()
    {
        var store = CreateStore();
        var machineId = MachineId.New();
        var day = Day(Site(), 7);
        var lineId = new ProductionLineId("LINE-E3-NEXT");
        await store.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(
                null,
                Roster(machineId, lineId, day, 1)),
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.CommitAsync(
                new MachineShiftOccurrenceRosterCommit(
                    new MachineShiftOccurrenceRosterRevision(1),
                    Roster(machineId, lineId, day, 3)),
                CancellationToken.None));

        var actual = Assert.IsType<MachineShiftOccurrenceRoster>(
            await store.ReadAsync(machineId, day, CancellationToken.None));
        Assert.Equal(1UL, actual.Revision.Value);
    }

    [Fact]
    public async Task ExistingIdentityCannotBeRehomedToDifferentProductionLine()
    {
        var store = CreateStore();
        var machineId = MachineId.New();
        var day = Day(Site(), 8);
        var originalLine = new ProductionLineId("LINE-E3-ORIGINAL");
        await store.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(
                null,
                Roster(machineId, originalLine, day, 1)),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.CommitAsync(
                new MachineShiftOccurrenceRosterCommit(
                    new MachineShiftOccurrenceRosterRevision(1),
                    Roster(
                        machineId,
                        new ProductionLineId("LINE-E3-OTHER"),
                        day,
                        2)),
                CancellationToken.None));

        Assert.Contains("production line", exception.Message, StringComparison.OrdinalIgnoreCase);
        var actual = Assert.IsType<MachineShiftOccurrenceRoster>(
            await store.ReadAsync(machineId, day, CancellationToken.None));
        Assert.Equal(originalLine, actual.ProductionLineId);
        Assert.Equal(1UL, actual.Revision.Value);
    }

    [Fact]
    public async Task SameShiftOccurrenceCannotBelongToConflictingProductionDays()
    {
        var store = CreateStore();
        var siteId = Site();
        var firstDay = Day(siteId, 9);
        var secondDay = Day(siteId, 10);
        var firstMachine = MachineId.New();
        var secondMachine = MachineId.New();
        var firstLine = new ProductionLineId("LINE-E3-OWN-A");
        var secondLine = new ProductionLineId("LINE-E3-OWN-B");
        var occurrenceId = new ShiftOccurrenceId(
            siteId,
            new ShiftScheduleAssignmentId("ASSIGN-E3-SHARED"),
            new ShiftId("SHIFT-E3-SHARED"),
            new DateTimeOffset(2026, 9, 9, 6, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 9, 8, 0, 0, TimeSpan.Zero));

        await store.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(
                null,
                Roster(
                    firstMachine,
                    firstLine,
                    firstDay,
                    1,
                    new MachineShiftOccurrenceOwnership(
                        firstMachine,
                        firstLine,
                        occurrenceId,
                        firstDay))),
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.CommitAsync(
                new MachineShiftOccurrenceRosterCommit(
                    null,
                    Roster(
                        secondMachine,
                        secondLine,
                        secondDay,
                        1,
                        new MachineShiftOccurrenceOwnership(
                            secondMachine,
                            secondLine,
                            occurrenceId,
                            secondDay))),
                CancellationToken.None));

        Assert.Null(await store.ReadAsync(secondMachine, secondDay, CancellationToken.None));
    }

    [Fact]
    public async Task SameAssignmentShiftAndTimesOnDifferentSitesAreDistinctOccurrences()
    {
        var store = CreateStore();
        var firstSite = Site();
        var secondSite = Site();
        var firstDay = Day(firstSite, 11);
        var secondDay = Day(secondSite, 12);
        var firstMachine = MachineId.New();
        var secondMachine = MachineId.New();
        var firstLine = new ProductionLineId("LINE-E3-SITE-A");
        var secondLine = new ProductionLineId("LINE-E3-SITE-B");
        var starts = new DateTimeOffset(2026, 9, 11, 6, 0, 0, TimeSpan.Zero);
        var ends = starts.AddHours(2);

        await store.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(
                null,
                Roster(
                    firstMachine,
                    firstLine,
                    firstDay,
                    1,
                    Ownership(firstMachine, firstLine, firstDay, "SAME", "SAME", starts, ends))),
            CancellationToken.None);
        await store.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(
                null,
                Roster(
                    secondMachine,
                    secondLine,
                    secondDay,
                    1,
                    Ownership(secondMachine, secondLine, secondDay, "SAME", "SAME", starts, ends))),
            CancellationToken.None);

        Assert.NotNull(await store.ReadAsync(firstMachine, firstDay, CancellationToken.None));
        Assert.NotNull(await store.ReadAsync(secondMachine, secondDay, CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentInitialCommitsHaveSingleWinner()
    {
        var store = CreateStore();
        var machineId = MachineId.New();
        var day = Day(Site(), 13);
        var lineId = new ProductionLineId("LINE-E3-CONCURRENT-INITIAL");
        var commit = new MachineShiftOccurrenceRosterCommit(
            null,
            Roster(machineId, lineId, day, 1));

        var results = await Task.WhenAll(
            CaptureCommitAsync(store, commit),
            CaptureCommitAsync(store, commit));

        Assert.Single(results, static exception => exception is null);
        var loser = Assert.Single(results, static exception => exception is not null);
        Assert.IsType<InvalidOperationException>(loser);
        var actual = Assert.IsType<MachineShiftOccurrenceRoster>(
            await store.ReadAsync(machineId, day, CancellationToken.None));
        Assert.Equal(1UL, actual.Revision.Value);
    }

    [Fact]
    public async Task ConcurrentReplacementCommitsHaveSingleWinner()
    {
        var store = CreateStore();
        var machineId = MachineId.New();
        var day = Day(Site(), 14);
        var lineId = new ProductionLineId("LINE-E3-CONCURRENT-REPLACE");
        await store.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(
                null,
                Roster(machineId, lineId, day, 1)),
            CancellationToken.None);
        var commit = new MachineShiftOccurrenceRosterCommit(
            new MachineShiftOccurrenceRosterRevision(1),
            Roster(machineId, lineId, day, 2));

        var results = await Task.WhenAll(
            CaptureCommitAsync(store, commit),
            CaptureCommitAsync(store, commit));

        Assert.Single(results, static exception => exception is null);
        var loser = Assert.Single(results, static exception => exception is not null);
        Assert.IsType<InvalidOperationException>(loser);
        var actual = Assert.IsType<MachineShiftOccurrenceRoster>(
            await store.ReadAsync(machineId, day, CancellationToken.None));
        Assert.Equal(2UL, actual.Revision.Value);
    }

    [Fact]
    public async Task PreCancelledReadAndCommitPropagateCancellationWithoutMutation()
    {
        var store = CreateStore();
        var machineId = MachineId.New();
        var day = Day(Site(), 15);
        var lineId = new ProductionLineId("LINE-E3-CANCEL");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await store.ReadAsync(machineId, day, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await store.CommitAsync(
                new MachineShiftOccurrenceRosterCommit(
                    null,
                    Roster(machineId, lineId, day, 1)),
                cancellation.Token));

        Assert.Null(await store.ReadAsync(machineId, day, CancellationToken.None));
    }

    private static async Task<Exception?> CaptureCommitAsync(
        IMachineShiftOccurrenceRosterStore store,
        MachineShiftOccurrenceRosterCommit commit)
    {
        try
        {
            await store.CommitAsync(commit, CancellationToken.None);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static SiteId Site() =>
        new($"E3-{Guid.NewGuid():N}");

    private static ProductionDayId Day(SiteId siteId, int day) =>
        new(siteId, new DateOnly(2026, 9, day));

    private static MachineShiftOccurrenceRoster Roster(
        MachineId machineId,
        ProductionLineId lineId,
        ProductionDayId productionDayId,
        ulong revision,
        params MachineShiftOccurrenceOwnership[] occurrences) =>
        new(
            machineId,
            lineId,
            productionDayId,
            new MachineShiftOccurrenceRosterRevision(revision),
            occurrences);

    private static MachineShiftOccurrenceOwnership Ownership(
        MachineId machineId,
        ProductionLineId lineId,
        ProductionDayId productionDayId,
        string assignmentId,
        string shiftId,
        int startHour,
        int endHour) =>
        Ownership(
            machineId,
            lineId,
            productionDayId,
            assignmentId,
            shiftId,
            new DateTimeOffset(
                2026,
                9,
                productionDayId.BusinessDate.Day,
                startHour,
                0,
                0,
                TimeSpan.Zero),
            new DateTimeOffset(
                2026,
                9,
                productionDayId.BusinessDate.Day,
                endHour,
                0,
                0,
                TimeSpan.Zero));

    private static MachineShiftOccurrenceOwnership Ownership(
        MachineId machineId,
        ProductionLineId lineId,
        ProductionDayId productionDayId,
        string assignmentId,
        string shiftId,
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc) =>
        new(
            machineId,
            lineId,
            new ShiftOccurrenceId(
                productionDayId.SiteId,
                new ShiftScheduleAssignmentId(assignmentId),
                new ShiftId(shiftId),
                startsAtUtc,
                endsAtUtc),
            productionDayId);
}
