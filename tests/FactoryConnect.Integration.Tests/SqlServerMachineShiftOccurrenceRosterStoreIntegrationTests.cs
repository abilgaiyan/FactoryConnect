using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerMachineShiftOccurrenceRosterStoreIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerMachineShiftOccurrenceRosterStoreIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task UnknownExactIdentityReturnsAbsent()
    {
        var store = CreateStore();
        var result = await store.ReadAsync(
            MachineId.New(),
            Day("E2-ABSENT", 1),
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task InitialInsertRoundTripsAndReconstructsCanonicalOccurrenceOrder()
    {
        var machineId = MachineId.New();
        var day = Day("E2-ROUNDTRIP", 2);
        var line = new ProductionLineId("LINE-E2-A");
        var later = Ownership(machineId, line, day, "ASSIGN-B", "SHIFT-B", 14, 16);
        var earlier = Ownership(machineId, line, day, "ASSIGN-A", "SHIFT-A", 6, 8);
        var proposed = Roster(machineId, line, day, 1, later, earlier);
        var store = CreateStore();

        await store.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(null, proposed),
            CancellationToken.None);

        var actual = Assert.IsType<MachineShiftOccurrenceRoster>(
            await store.ReadAsync(machineId, day, CancellationToken.None));
        Assert.Equal(machineId, actual.MachineId);
        Assert.Equal(line, actual.ProductionLineId);
        Assert.Equal(day, actual.ProductionDayId);
        Assert.Equal(1UL, actual.Revision.Value);
        Assert.Equal(2, actual.Occurrences.Count);
        Assert.Equal("ASSIGN-A", actual.Occurrences[0].ShiftOccurrenceId.ShiftScheduleAssignmentId.Value);
        Assert.Equal("ASSIGN-B", actual.Occurrences[1].ShiftOccurrenceId.ShiftScheduleAssignmentId.Value);
    }

    [Fact]
    public async Task EmptyRosterRoundTripsAndCanBeReplacedByNextEmptyRevision()
    {
        var machineId = MachineId.New();
        var day = Day("E2-EMPTY", 3);
        var line = new ProductionLineId("LINE-E2-EMPTY");
        var store = CreateStore();

        await store.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(
                null,
                Roster(machineId, line, day, 1)),
            CancellationToken.None);
        await store.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(
                new MachineShiftOccurrenceRosterRevision(1),
                Roster(machineId, line, day, 2)),
            CancellationToken.None);

        var actual = Assert.IsType<MachineShiftOccurrenceRoster>(
            await store.ReadAsync(machineId, day, CancellationToken.None));
        Assert.Equal(2UL, actual.Revision.Value);
        Assert.Empty(actual.Occurrences);
    }

    [Fact]
    public async Task ReplacementUsesRevisionCasAndCompletelyReplacesOccurrences()
    {
        var machineId = MachineId.New();
        var day = Day("E2-REPLACE", 4);
        var line = new ProductionLineId("LINE-E2-REPLACE");
        var store = CreateStore();
        await store.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(
                null,
                Roster(
                    machineId,
                    line,
                    day,
                    1,
                    Ownership(machineId, line, day, "OLD", "OLD", 6, 8))),
            CancellationToken.None);

        await store.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(
                new MachineShiftOccurrenceRosterRevision(1),
                Roster(
                    machineId,
                    line,
                    day,
                    2,
                    Ownership(machineId, line, day, "NEW-B", "NEW-B", 14, 16),
                    Ownership(machineId, line, day, "NEW-A", "NEW-A", 10, 12))),
            CancellationToken.None);

        var actual = Assert.IsType<MachineShiftOccurrenceRoster>(
            await store.ReadAsync(machineId, day, CancellationToken.None));
        Assert.Equal(2UL, actual.Revision.Value);
        Assert.Equal(
            ["NEW-A", "NEW-B"],
            actual.Occurrences
                .Select(static item => item.ShiftOccurrenceId.ShiftScheduleAssignmentId.Value)
                .ToArray());
    }

    [Fact]
    public async Task StaleRevisionAndProductionLineRehomeAreRejected()
    {
        var machineId = MachineId.New();
        var day = Day("E2-CAS", 5);
        var line = new ProductionLineId("LINE-E2-CAS");
        var store = CreateStore();
        await store.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(null, Roster(machineId, line, day, 1)),
            CancellationToken.None);

        var stale = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.CommitAsync(
                new MachineShiftOccurrenceRosterCommit(
                    null,
                    Roster(machineId, line, day, 2)),
                CancellationToken.None));
        Assert.Contains("revision conflict", stale.Message, StringComparison.OrdinalIgnoreCase);

        var rehome = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.CommitAsync(
                new MachineShiftOccurrenceRosterCommit(
                    new MachineShiftOccurrenceRosterRevision(1),
                    Roster(
                        machineId,
                        new ProductionLineId("LINE-E2-OTHER"),
                        day,
                        2)),
                CancellationToken.None));
        Assert.Contains("production line", rehome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConflictingProductionDayOwnershipIsRejectedWithoutMutation()
    {
        var machineA = MachineId.New();
        var machineB = MachineId.New();
        var dayA = Day("E2-OWNERSHIP", 6);
        var dayB = Day("E2-OWNERSHIP", 7);
        var lineA = new ProductionLineId("LINE-E2-OWN-A");
        var lineB = new ProductionLineId("LINE-E2-OWN-B");
        var store = CreateStore();
        var occurrenceA = Ownership(machineA, lineA, dayA, "SAME", "SHIFT", 6, 8);
        var occurrenceB = new MachineShiftOccurrenceOwnership(
            machineB,
            lineB,
            new ShiftOccurrenceId(
                dayB.SiteId,
                occurrenceA.ShiftOccurrenceId.ShiftScheduleAssignmentId,
                occurrenceA.ShiftOccurrenceId.ShiftId,
                occurrenceA.ShiftOccurrenceId.StartsAtUtc,
                occurrenceA.ShiftOccurrenceId.EndsAtUtc),
            dayB);

        await store.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(
                null,
                Roster(machineA, lineA, dayA, 1, occurrenceA)),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.CommitAsync(
                new MachineShiftOccurrenceRosterCommit(
                    null,
                    Roster(machineB, lineB, dayB, 1, occurrenceB)),
                CancellationToken.None));

        Assert.Contains("conflicting production days", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await store.ReadAsync(machineB, dayB, CancellationToken.None));
    }

    [Fact]
    public async Task FailureAfterRevisionAdvanceAndChildDeleteRollsBackCompleteRoster()
    {
        var machineId = MachineId.New();
        var day = Day("E2-ROLLBACK", 8);
        var line = new ProductionLineId("LINE-E2-ROLLBACK");
        var oldOccurrence = Ownership(machineId, line, day, "OLD-ROLLBACK", "OLD-ROLLBACK", 6, 8);
        var failingShift = $"FAIL-{Guid.NewGuid():N}";
        var store = CreateStore();
        await store.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(
                null,
                Roster(machineId, line, day, 1, oldOccurrence)),
            CancellationToken.None);
        await CreateFailureTriggerAsync(failingShift);

        try
        {
            var exception = await Assert.ThrowsAsync<SqlException>(async () =>
                await store.CommitAsync(
                    new MachineShiftOccurrenceRosterCommit(
                        new MachineShiftOccurrenceRosterRevision(1),
                        Roster(
                            machineId,
                            line,
                            day,
                            2,
                            Ownership(machineId, line, day, "FAIL-ASSIGN", failingShift, 10, 12))),
                    CancellationToken.None));
            Assert.Equal(51000, exception.Number);
        }
        finally
        {
            await DropFailureTriggerAsync();
        }

        var actual = Assert.IsType<MachineShiftOccurrenceRoster>(
            await store.ReadAsync(machineId, day, CancellationToken.None));
        Assert.Equal(1UL, actual.Revision.Value);
        var occurrence = Assert.Single(actual.Occurrences);
        Assert.Equal("OLD-ROLLBACK", occurrence.ShiftOccurrenceId.ShiftId.Value);
    }

    [Fact]
    public async Task ConcurrentInitialCommitsAllowOneWinnerAndOneRevisionConflict()
    {
        var machineId = MachineId.New();
        var day = Day("E2-CONCURRENT-INITIAL", 9);
        var line = new ProductionLineId("LINE-E2-CONCURRENT-INITIAL");
        var first = new MachineShiftOccurrenceRosterCommit(
            null,
            Roster(machineId, line, day, 1));
        var second = new MachineShiftOccurrenceRosterCommit(
            null,
            Roster(machineId, line, day, 1));

        var results = await Task.WhenAll(
            CaptureCommitAsync(CreateStore(), first),
            CaptureCommitAsync(CreateStore(), second));

        Assert.Single(results, static exception => exception is null);
        var loser = Assert.Single(results, static exception => exception is not null);
        var conflict = Assert.IsType<InvalidOperationException>(loser);
        Assert.Contains("revision conflict", conflict.Message, StringComparison.OrdinalIgnoreCase);
        var actual = Assert.IsType<MachineShiftOccurrenceRoster>(
            await CreateStore().ReadAsync(machineId, day, CancellationToken.None));
        Assert.Equal(1UL, actual.Revision.Value);
    }

    [Fact]
    public async Task ConcurrentReplacementCommitsAllowOneWinnerAndOneRevisionConflict()
    {
        var machineId = MachineId.New();
        var day = Day("E2-CONCURRENT-REPLACE", 10);
        var line = new ProductionLineId("LINE-E2-CONCURRENT-REPLACE");
        var seedStore = CreateStore();
        await seedStore.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(null, Roster(machineId, line, day, 1)),
            CancellationToken.None);
        var expected = new MachineShiftOccurrenceRosterRevision(1);
        var first = new MachineShiftOccurrenceRosterCommit(
            expected,
            Roster(machineId, line, day, 2));
        var second = new MachineShiftOccurrenceRosterCommit(
            expected,
            Roster(machineId, line, day, 2));

        var results = await Task.WhenAll(
            CaptureCommitAsync(CreateStore(), first),
            CaptureCommitAsync(CreateStore(), second));

        Assert.Single(results, static exception => exception is null);
        var loser = Assert.Single(results, static exception => exception is not null);
        var conflict = Assert.IsType<InvalidOperationException>(loser);
        Assert.Contains("revision conflict", conflict.Message, StringComparison.OrdinalIgnoreCase);
        var actual = Assert.IsType<MachineShiftOccurrenceRoster>(
            await CreateStore().ReadAsync(machineId, day, CancellationToken.None));
        Assert.Equal(2UL, actual.Revision.Value);
    }

    [Fact]
    public async Task ReadRejectsParentAndOccurrenceTextOrderKeyCorruption()
    {
        var parentMachine = MachineId.New();
        var parentDay = Day("E2-CORRUPT-PARENT", 11);
        var parentLine = new ProductionLineId("LINE-E2-CORRUPT-PARENT");
        var store = CreateStore();
        await store.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(
                null,
                Roster(parentMachine, parentLine, parentDay, 1)),
            CancellationToken.None);
        await ExecuteAsync(
            """
            UPDATE dbo.MachineShiftOccurrenceRoster
            SET ProductionDaySiteId = N'E2-CORRUPT-PARENT-TEXT'
            WHERE MachineId = @MachineId;
            """,
            ("@MachineId", SqlDbType.UniqueIdentifier, parentMachine.Value));

        var parentException = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.ReadAsync(parentMachine, parentDay, CancellationToken.None));
        Assert.Contains("identity", parentException.Message, StringComparison.OrdinalIgnoreCase);

        var childMachine = MachineId.New();
        var childDay = Day("E2-CORRUPT-CHILD", 12);
        var childLine = new ProductionLineId("LINE-E2-CORRUPT-CHILD");
        await store.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(
                null,
                Roster(
                    childMachine,
                    childLine,
                    childDay,
                    1,
                    Ownership(childMachine, childLine, childDay, "CHILD", "SHIFT-ORIGINAL", 6, 8))),
            CancellationToken.None);
        await ExecuteAsync(
            """
            UPDATE o
            SET ShiftId = N'SHIFT-CORRUPTED'
            FROM dbo.MachineShiftOccurrenceRosterOccurrence AS o
            INNER JOIN dbo.MachineShiftOccurrenceRoster AS r
                ON r.MachineShiftOccurrenceRosterRowId = o.MachineShiftOccurrenceRosterRowId
            WHERE r.MachineId = @MachineId;
            """,
            ("@MachineId", SqlDbType.UniqueIdentifier, childMachine.Value));

        var childException = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.ReadAsync(childMachine, childDay, CancellationToken.None));
        Assert.Contains("order key", childException.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreCancelledReadAndCommitPropagateCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var machineId = MachineId.New();
        var day = Day("E2-CANCEL", 13);
        var line = new ProductionLineId("LINE-E2-CANCEL");
        var store = CreateStore();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await store.ReadAsync(machineId, day, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await store.CommitAsync(
                new MachineShiftOccurrenceRosterCommit(
                    null,
                    Roster(machineId, line, day, 1)),
                cancellation.Token));
    }

    private SqlServerMachineShiftOccurrenceRosterStore CreateStore() =>
        new(_fixture.ConnectionString);

    private static ProductionDayId Day(string siteId, int day) =>
        new(new SiteId(siteId), new DateOnly(2026, 9, day));

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
        new(
            machineId,
            lineId,
            new ShiftOccurrenceId(
                productionDayId.SiteId,
                new ShiftScheduleAssignmentId(assignmentId),
                new ShiftId(shiftId),
                new DateTimeOffset(2026, 9, productionDayId.BusinessDate.Day, startHour, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 9, productionDayId.BusinessDate.Day, endHour, 0, 0, TimeSpan.Zero)),
            productionDayId);

    private static async Task<Exception?> CaptureCommitAsync(
        SqlServerMachineShiftOccurrenceRosterStore store,
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

    private async Task CreateFailureTriggerAsync(string failingShiftId)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TRIGGER dbo.TR_FC030_E2_RosterRollbackProbe
            ON dbo.MachineShiftOccurrenceRosterOccurrence
            AFTER INSERT
            AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS
                (
                    SELECT 1
                    FROM inserted
                    WHERE ShiftId = N'{EscapeLiteral(failingShiftId)}'
                )
                    THROW 51000, 'FC-030.2E rollback probe.', 1;
            END;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private async Task DropFailureTriggerAsync()
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DROP TRIGGER IF EXISTS dbo.TR_FC030_E2_RosterRollbackProbe;";
        await command.ExecuteNonQueryAsync();
    }

    private async Task ExecuteAsync(
        string sql,
        params (string Name, SqlDbType Type, object Value)[] parameters)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter.Name, parameter.Type).Value = parameter.Value;
        }

        await command.ExecuteNonQueryAsync();
    }

    private static string EscapeLiteral(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);
}
