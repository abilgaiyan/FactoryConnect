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
        // Seed qualified receipt and independent verification; publish through the actual writer.
        await using (var setup = new SqlCommand("""
            DECLARE @receipt uniqueidentifier = NEWID(), @verification uniqueidentifier = NEWID();
            INSERT dbo.SupervisorProductionQualificationReceipt
                (ReceiptId, EvidenceId, EvidenceKind, PopulationKey, MachineId,
                 ShiftScheduleAssignmentId, ShiftId, ShiftStartsAtUtc, ShiftEndsAtUtc,
                 ProductionOrder, PartNumber, Operation, QuantityUnit, ProducedQuantity,
                 GoodQuantity, RejectedQuantity, QualificationDecision, AuthorityReference, QualifiedAtUtc)
            VALUES (@receipt, NEWID(), N'SupervisorRevision', N'population', @machine,
                    N'assignment', N'shift', @start, @end, N'WO-1', N'GEAR-1', N'OP-10',
                    N'pieces', 120, 114, 6, N'Qualified', N'test-only', @end);
            INSERT dbo.SupervisorAcceptedProductionVerification
                (VerificationId, ReceiptId, VerifiedProducedQuantity, Coverage, AuthorityReference, VerifiedAtUtc)
            VALUES (@verification, @receipt, 120, N'Complete', N'test-only', @end);
            EXEC dbo.PublishSupervisorAcceptedProduction @publication, @receipt, @verification;
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

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var reviewStore = new SqlServerSupervisorCycleReviewStore(database.ConnectionString);
        var review = new SupervisorCycleReview(Guid.NewGuid(), cycle, SupervisorCycleReviewDecision.Accepted,
            "reviewer", start.AddHours(1));
        var reads = Enumerable.Range(0, 8).Select(_ =>
            reader.ReadAsync(machine, "assignment", "shift", start, end, timeout.Token)).ToArray();
        var write = reviewStore.AppendAsync(review, timeout.Token);
        var concurrent = await Task.WhenAll(reads);
        Assert.True(await write);
        foreach (var result in concurrent)
        {
            Assert.Equal(publication, result.PublicationId);
            Assert.Equal("accepted-quantity-preview", result.Status);
            if (result.CycleSource == "accepted-override")
            {
                Assert.Equal(57600m, result.IdealProductionSeconds);
                Assert.Equal(cycle, result.CycleRevisionId);
                Assert.Null(result.DefaultSnapshotId);
            }
            else
            {
                Assert.Equal(expectedSource, result.CycleSource);
                Assert.Equal((decimal)expectedSeconds, result.IdealProductionSeconds);
                Assert.Equal(snapshot, result.DefaultSnapshotId);
                Assert.Null(result.CycleRevisionId);
            }
        }
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
