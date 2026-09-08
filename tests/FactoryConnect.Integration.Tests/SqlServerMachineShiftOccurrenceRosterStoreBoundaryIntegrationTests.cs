using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerMachineShiftOccurrenceRosterStoreBoundaryIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerMachineShiftOccurrenceRosterStoreBoundaryIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SameOccurrenceCoordinatesOnDifferentSitesRemainDistinctIdentities()
    {
        var machineA = MachineId.New();
        var machineB = MachineId.New();
        var businessDate = new DateOnly(2026, 9, 14);
        var dayA = new ProductionDayId(new SiteId("E2-SITE-A"), businessDate);
        var dayB = new ProductionDayId(new SiteId("E2-SITE-B"), businessDate);
        var lineA = new ProductionLineId("LINE-E2-SITE-A");
        var lineB = new ProductionLineId("LINE-E2-SITE-B");
        var startsAtUtc = new DateTimeOffset(2026, 9, 14, 6, 0, 0, TimeSpan.Zero);
        var endsAtUtc = new DateTimeOffset(2026, 9, 14, 8, 0, 0, TimeSpan.Zero);
        var assignmentId = new ShiftScheduleAssignmentId("ASSIGN-SHARED");
        var shiftId = new ShiftId("SHIFT-SHARED");
        var store = CreateStore();

        var occurrenceA = new MachineShiftOccurrenceOwnership(
            machineA,
            lineA,
            new ShiftOccurrenceId(dayA.SiteId, assignmentId, shiftId, startsAtUtc, endsAtUtc),
            dayA);
        var occurrenceB = new MachineShiftOccurrenceOwnership(
            machineB,
            lineB,
            new ShiftOccurrenceId(dayB.SiteId, assignmentId, shiftId, startsAtUtc, endsAtUtc),
            dayB);

        await store.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(
                null,
                Roster(machineA, lineA, dayA, 1, occurrenceA)),
            CancellationToken.None);
        await store.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(
                null,
                Roster(machineB, lineB, dayB, 1, occurrenceB)),
            CancellationToken.None);

        var actualA = Assert.IsType<MachineShiftOccurrenceRoster>(
            await store.ReadAsync(machineA, dayA, CancellationToken.None));
        var actualB = Assert.IsType<MachineShiftOccurrenceRoster>(
            await store.ReadAsync(machineB, dayB, CancellationToken.None));
        Assert.Single(actualA.Occurrences);
        Assert.Single(actualB.Occurrences);
        Assert.NotEqual(
            actualA.Occurrences[0].ShiftOccurrenceId,
            actualB.Occurrences[0].ShiftOccurrenceId);
    }

    [Fact]
    public async Task ReadRejectsPersistedCrossProductionDayOccurrenceOwnershipCorruption()
    {
        var machineA = MachineId.New();
        var machineB = MachineId.New();
        var dayA = Day("E2-CORRUPT-OWNERSHIP", 15);
        var dayB = Day("E2-CORRUPT-OWNERSHIP", 16);
        var lineA = new ProductionLineId("LINE-E2-CORRUPT-OWN-A");
        var lineB = new ProductionLineId("LINE-E2-CORRUPT-OWN-B");
        var occurrence = Ownership(
            machineA,
            lineA,
            dayA,
            "CORRUPT-ASSIGN",
            "CORRUPT-SHIFT",
            new DateTimeOffset(2026, 9, 15, 6, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 15, 8, 0, 0, TimeSpan.Zero));
        var store = CreateStore();

        await store.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(
                null,
                Roster(machineA, lineA, dayA, 1, occurrence)),
            CancellationToken.None);
        await store.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(
                null,
                Roster(machineB, lineB, dayB, 1)),
            CancellationToken.None);

        await using (var connection = _fixture.CreateConnection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO dbo.MachineShiftOccurrenceRosterOccurrence
                    (MachineShiftOccurrenceRosterRowId,
                     ShiftScheduleAssignmentId,
                     ShiftScheduleAssignmentOrderKey,
                     ShiftId,
                     ShiftOrderKey,
                     ShiftStartsAtUtc,
                     ShiftEndsAtUtc)
                SELECT
                    r.MachineShiftOccurrenceRosterRowId,
                    @AssignmentId,
                    @AssignmentOrderKey,
                    @ShiftId,
                    @ShiftOrderKey,
                    @StartsAtUtc,
                    @EndsAtUtc
                FROM dbo.MachineShiftOccurrenceRoster AS r
                WHERE r.MachineId = @MachineId
                  AND r.ProductionDaySiteOrderKey = @SiteOrderKey
                  AND r.ProductionBusinessDate = @BusinessDate;
                """;
            command.Parameters.Add("@AssignmentId", SqlDbType.NVarChar, 256).Value =
                occurrence.ShiftOccurrenceId.ShiftScheduleAssignmentId.Value;
            command.Parameters.Add(
                "@AssignmentOrderKey",
                SqlDbType.VarBinary,
                StringOrderKeyV2Codec.MaximumEncodedLength).Value =
                StringOrderKeyV2Codec.Encode(
                    occurrence.ShiftOccurrenceId.ShiftScheduleAssignmentId.Value);
            command.Parameters.Add("@ShiftId", SqlDbType.NVarChar, 256).Value =
                occurrence.ShiftOccurrenceId.ShiftId.Value;
            command.Parameters.Add(
                "@ShiftOrderKey",
                SqlDbType.VarBinary,
                StringOrderKeyV2Codec.MaximumEncodedLength).Value =
                StringOrderKeyV2Codec.Encode(occurrence.ShiftOccurrenceId.ShiftId.Value);
            command.Parameters.Add("@StartsAtUtc", SqlDbType.DateTimeOffset).Value =
                occurrence.ShiftOccurrenceId.StartsAtUtc;
            command.Parameters.Add("@EndsAtUtc", SqlDbType.DateTimeOffset).Value =
                occurrence.ShiftOccurrenceId.EndsAtUtc;
            command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value = machineB.Value;
            command.Parameters.Add(
                "@SiteOrderKey",
                SqlDbType.VarBinary,
                StringOrderKeyV2Codec.MaximumEncodedLength).Value =
                StringOrderKeyV2Codec.Encode(dayB.SiteId.Value);
            command.Parameters.Add("@BusinessDate", SqlDbType.Date).Value =
                dayB.BusinessDate.ToDateTime(TimeOnly.MinValue);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.ReadAsync(machineA, dayA, CancellationToken.None));
        Assert.Contains(
            "conflicting production days",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BlockedReadPropagatesInFlightCancellation()
    {
        var machineId = MachineId.New();
        var day = Day("E2-CANCEL-READ", 17);
        var line = new ProductionLineId("LINE-E2-CANCEL-READ");
        var store = CreateStore();
        await store.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(
                null,
                Roster(machineId, line, day, 1)),
            CancellationToken.None);

        await using var blocker = _fixture.CreateConnection();
        await blocker.OpenAsync();
        await using var transaction = blocker.BeginTransaction();
        await HoldRosterWriteLockAsync(blocker, transaction, machineId, day);
        using var cancellation = new CancellationTokenSource();

        var readTask = store.ReadAsync(machineId, day, cancellation.Token).AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(150));
        Assert.False(readTask.IsCompleted);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task BlockedCommitLockAcquisitionPropagatesCancellationWithoutMutation()
    {
        var machineId = MachineId.New();
        var day = Day("E2-CANCEL-COMMIT", 18);
        var line = new ProductionLineId("LINE-E2-CANCEL-COMMIT");
        var original = Ownership(
            machineId,
            line,
            day,
            "CANCEL-ORIGINAL",
            "CANCEL-ORIGINAL",
            new DateTimeOffset(2026, 9, 18, 6, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 18, 8, 0, 0, TimeSpan.Zero));
        var store = CreateStore();
        await store.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(
                null,
                Roster(machineId, line, day, 1, original)),
            CancellationToken.None);

        await using var blocker = _fixture.CreateConnection();
        await blocker.OpenAsync();
        await using var transaction = blocker.BeginTransaction();
        await HoldRosterWriteLockAsync(blocker, transaction, machineId, day);
        using var cancellation = new CancellationTokenSource();
        var replacement = new MachineShiftOccurrenceRosterCommit(
            new MachineShiftOccurrenceRosterRevision(1),
            Roster(machineId, line, day, 2));

        var commitTask = store.CommitAsync(replacement, cancellation.Token).AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(150));
        Assert.False(commitTask.IsCompleted);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => commitTask);
        await transaction.RollbackAsync();

        var actual = Assert.IsType<MachineShiftOccurrenceRoster>(
            await store.ReadAsync(machineId, day, CancellationToken.None));
        Assert.Equal(1UL, actual.Revision.Value);
        var actualOccurrence = Assert.Single(actual.Occurrences);
        Assert.Equal(
            "CANCEL-ORIGINAL",
            actualOccurrence.ShiftOccurrenceId.ShiftId.Value);
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

    private static async Task HoldRosterWriteLockAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        MachineId machineId,
        ProductionDayId day)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE dbo.MachineShiftOccurrenceRoster
            SET Revision = Revision
            WHERE MachineId = @MachineId
              AND ProductionDaySiteOrderKey = @SiteOrderKey
              AND ProductionBusinessDate = @BusinessDate;
            """;
        command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value = machineId.Value;
        command.Parameters.Add(
            "@SiteOrderKey",
            SqlDbType.VarBinary,
            StringOrderKeyV2Codec.MaximumEncodedLength).Value =
            StringOrderKeyV2Codec.Encode(day.SiteId.Value);
        command.Parameters.Add("@BusinessDate", SqlDbType.Date).Value =
            day.BusinessDate.ToDateTime(TimeOnly.MinValue);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }
}
