using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerSupervisorAcceptedIdealTimeIntegrationTests
{
    [Theory]
    [InlineData(true, "operation-default", 64800)]
    [InlineData(false, "part-default", 72000)]
    public async Task CapturedDefaultAndAcceptedCorrectionAreReadFromSql(
        bool operationDefault, string expectedSource, int expectedSeconds)
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await SqlServerMigrationEngine.CreateDefault().ApplyAsync(
            connection, TimeSpan.FromMinutes(2), CancellationToken.None);

        var machine = Guid.NewGuid();
        var publication = Guid.NewGuid();
        var snapshot = Guid.NewGuid();
        var cycle = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddHours(8);
        // Synthetic projection fixture: tests reader behavior, not publication ancestry.
        await using (var setup = new SqlCommand("""
            INSERT dbo.SupervisorAcceptedShiftProduction
                (PublicationId, MachineId, ShiftScheduleAssignmentId, ShiftId,
                 ShiftStartsAtUtc, ShiftEndsAtUtc, ProductionOrder, PartNumber, Operation,
                 QuantityUnit, AcceptedProducedQuantity, Coverage, AuthorityReference, PublishedAtUtc)
            VALUES (@publication, @machine, N'assignment', N'shift', @start, @end,
                    N'WO-1', N'GEAR-1', N'OP-10', N'pieces', 120, N'Complete', N'test-only', @end);
            INSERT dbo.SupervisorCycleDefaultSnapshot
                (SnapshotId, MachineId, ShiftScheduleAssignmentId, ShiftId,
                 ShiftStartsAtUtc, ShiftEndsAtUtc, ProductionOrder, PartNumber, Operation,
                 PartSeconds, OperationSeconds, CapturedAtUtc)
            VALUES (@snapshot, @machine, N'assignment', N'shift', @start, @end,
                    N'WO-1', N'GEAR-1', N'OP-10', 600, @operation, @start);
            INSERT dbo.SupervisorShiftIdealCycle
                (CycleRevisionId, MachineId, ShiftScheduleAssignmentId, ShiftId,
                 ShiftStartsAtUtc, ShiftEndsAtUtc, RevisionNumber, IdealCycleSeconds,
                 ProductionOrder, PartNumber, Operation, EnteredBy, RecordedAtUtc)
            VALUES (@cycle, @machine, N'assignment', N'shift', @start, @end,
                    1, 480, N'WO-1', N'GEAR-1', N'OP-10', N'supervisor', @start);
            """, connection))
        {
            setup.Parameters.Add("@machine", System.Data.SqlDbType.UniqueIdentifier).Value = machine;
            setup.Parameters.Add("@publication", System.Data.SqlDbType.UniqueIdentifier).Value = publication;
            setup.Parameters.Add("@snapshot", System.Data.SqlDbType.UniqueIdentifier).Value = snapshot;
            setup.Parameters.Add("@cycle", System.Data.SqlDbType.UniqueIdentifier).Value = cycle;
            setup.Parameters.Add("@start", System.Data.SqlDbType.DateTimeOffset).Value = start;
            setup.Parameters.Add("@end", System.Data.SqlDbType.DateTimeOffset).Value = end;
            setup.Parameters.Add("@operation", System.Data.SqlDbType.Decimal).Value =
                operationDefault ? (object)540m : DBNull.Value;
            await setup.ExecuteNonQueryAsync();
        }

        var reader = new SqlServerSupervisorAcceptedIdealTimeReader(database.ConnectionString);
        var before = await reader.ReadAsync(machine, "assignment", "shift", start, end);
        Assert.Equal("accepted-quantity-preview", before.Status);
        Assert.Equal((decimal)expectedSeconds, before.IdealProductionSeconds);
        Assert.Equal(expectedSource, before.CycleSource);
        Assert.Equal(snapshot, before.DefaultSnapshotId);
        Assert.Equal(publication, before.PublicationId);
        Assert.Null(before.CycleRevisionId);

        await new SqlServerSupervisorCycleReviewStore(database.ConnectionString).AppendAsync(
            new SupervisorCycleReview(Guid.NewGuid(), cycle, SupervisorCycleReviewDecision.Accepted,
                "reviewer", start.AddHours(1)));
        var after = await reader.ReadAsync(machine, "assignment", "shift", start, end);
        Assert.Equal(57600m, after.IdealProductionSeconds);
        Assert.Equal("accepted-override", after.CycleSource);
        Assert.Equal(cycle, after.CycleRevisionId);
        Assert.Equal(publication, after.PublicationId);
        Assert.Null(after.DefaultSnapshotId);

        var missing = await reader.ReadAsync(machine, "assignment", "other-shift", start, end);
        Assert.Equal("publication-unavailable", missing.Status);
        Assert.Null(missing.IdealProductionSeconds);
    }
}
