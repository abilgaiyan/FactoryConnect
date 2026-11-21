using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerAcceptedProductionSourceIntegrityTests
{
    [Theory]
    [InlineData("Rejected", false, false)]
    [InlineData("Qualified", true, false)]
    [InlineData("Qualified", false, true)]
    public async Task UnqualifiedOrMismatchedSourceCannotBeAttached(
        string decision, bool mismatchMachine, bool mismatchUnit)
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await SqlServerMigrationEngine.CreateDefault().ApplyAsync(
            connection, TimeSpan.FromMinutes(2), CancellationToken.None);

        var receipt = Guid.NewGuid();
        var publication = Guid.NewGuid();
        var machine = Guid.NewGuid();
        var publicationMachine = mismatchMachine ? Guid.NewGuid() : machine;
        var publicationUnit = mismatchUnit ? "cycles" : "pieces";

        await using (var setup = new SqlCommand("""
            INSERT dbo.SupervisorProductionQualificationReceipt
                (ReceiptId, EvidenceId, EvidenceKind, PopulationKey, MachineId,
                 ShiftScheduleAssignmentId, ShiftId, ShiftStartsAtUtc, ShiftEndsAtUtc,
                 QuantityUnit, ProducedQuantity, GoodQuantity, RejectedQuantity,
                 QualificationDecision, AuthorityReference, QualifiedAtUtc)
            VALUES (@receipt, @evidence, N'SupervisorRevision', N'population-one',
                    @machine, N'assignment-one', N'shift-1',
                    '2026-11-17T02:00:00+00:00', '2026-11-17T10:30:00+00:00',
                    N'pieces', 11, 10, 1, @decision, N'test-only',
                    '2026-11-17T11:00:00+00:00');
            INSERT dbo.SupervisorAcceptedShiftProduction
                (PublicationId, MachineId, ShiftScheduleAssignmentId, ShiftId,
                 ShiftStartsAtUtc, ShiftEndsAtUtc, QuantityUnit,
                 AcceptedProducedQuantity, Coverage, AuthorityReference, PublishedAtUtc)
            VALUES (@publication, @publicationMachine, N'assignment-one', N'shift-1',
                    '2026-11-17T02:00:00+00:00', '2026-11-17T10:30:00+00:00',
                    @publicationUnit, 11, N'Complete', N'test-only',
                    '2026-11-17T11:00:00+00:00');
            """, connection))
        {
            setup.Parameters.Add("@receipt", System.Data.SqlDbType.UniqueIdentifier).Value = receipt;
            setup.Parameters.Add("@evidence", System.Data.SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
            setup.Parameters.Add("@machine", System.Data.SqlDbType.UniqueIdentifier).Value = machine;
            setup.Parameters.Add("@publicationMachine", System.Data.SqlDbType.UniqueIdentifier).Value = publicationMachine;
            setup.Parameters.Add("@publication", System.Data.SqlDbType.UniqueIdentifier).Value = publication;
            setup.Parameters.Add("@decision", System.Data.SqlDbType.NVarChar, 20).Value = decision;
            setup.Parameters.Add("@publicationUnit", System.Data.SqlDbType.NVarChar, 50).Value = publicationUnit;
            await setup.ExecuteNonQueryAsync();
        }

        await using (var link = new SqlCommand(
            "INSERT dbo.SupervisorAcceptedShiftProductionSource(PublicationId, ReceiptId) VALUES (@publication, @receipt)", connection))
        {
            link.Parameters.Add("@publication", System.Data.SqlDbType.UniqueIdentifier).Value = publication;
            link.Parameters.Add("@receipt", System.Data.SqlDbType.UniqueIdentifier).Value = receipt;
            var error = await Assert.ThrowsAsync<SqlException>(
                () => link.ExecuteNonQueryAsync());
            Assert.Equal(51037, error.Number);
        }

        await using var count = new SqlCommand(
            "SELECT COUNT_BIG(*) FROM dbo.SupervisorAcceptedShiftProductionSource WHERE PublicationId = @publication", connection);
        count.Parameters.Add("@publication", System.Data.SqlDbType.UniqueIdentifier).Value = publication;
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
    }
}
