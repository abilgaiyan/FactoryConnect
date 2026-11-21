using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerMigration036InspectionTests
{
    [Fact]
    public async Task MissingReceiptIsExplicitAndInspectionDoesNotCreatePublication()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await SqlServerMigrationEngine.CreateDefault().ApplyAsync(
            connection, TimeSpan.FromMinutes(2), CancellationToken.None);

        var missingReceipt = Guid.NewGuid();
        await using (var command = new SqlCommand(
            "dbo.InspectSupervisorAcceptedProductionReceipt", connection))
        {
            command.CommandType = System.Data.CommandType.StoredProcedure;
            command.Parameters.Add("@ReceiptId", System.Data.SqlDbType.UniqueIdentifier).Value = missingReceipt;
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.IsDBNull(reader.GetOrdinal("ReceiptId")));
            Assert.Equal("ReceiptNotFound", reader.GetString(reader.GetOrdinal("InspectionStatus")));
            Assert.False(await reader.ReadAsync());
        }

        await using (var count = new SqlCommand(
            "SELECT (SELECT COUNT_BIG(*) FROM dbo.SupervisorAcceptedShiftProduction) + " +
            "(SELECT COUNT_BIG(*) FROM dbo.SupervisorAcceptedShiftProductionSource)", connection))
        {
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
        }

        // The full migration runner must be idempotent with the new procedure present.
        await SqlServerMigrationEngine.CreateDefault().ApplyAsync(
            connection, TimeSpan.FromMinutes(2), CancellationToken.None);
    }
    [Theory]
    [InlineData("Rejected", "NotQualified")]
    [InlineData("Qualified", "RequiresCoverageAndAuthorityVerification")]
    public async Task ReceiptDecisionCannotAuthorizePublication(string decision, string expectedStatus)
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await SqlServerMigrationEngine.CreateDefault().ApplyAsync(
            connection, TimeSpan.FromMinutes(2), CancellationToken.None);

        var receiptId = Guid.NewGuid();
        await using (var insert = new SqlCommand("""
            INSERT INTO dbo.SupervisorProductionQualificationReceipt
            (ReceiptId, EvidenceId, EvidenceKind, PopulationKey, MachineId,
             ShiftScheduleAssignmentId, ShiftId, ShiftStartsAtUtc, ShiftEndsAtUtc,
             ProductionOrder, PartNumber, Operation, QuantityUnit,
             ProducedQuantity, GoodQuantity, RejectedQuantity,
             QualificationDecision, AuthorityReference, QualifiedAtUtc)
            VALUES
            (@receipt, @evidence, N'SupervisorRevision', N'test-population',
             @machine, N'test-assignment', N'shift-1',
             '2026-11-17T02:00:00+00:00', '2026-11-17T10:30:00+00:00',
             N'PO-0001', N'P-0001', N'OP-0001', N'pieces',
             11, 10, 1, @decision, N'test-independent-authority',
             '2026-11-17T11:00:00+00:00');
            """, connection))
        {
            insert.Parameters.Add("@receipt", System.Data.SqlDbType.UniqueIdentifier).Value = receiptId;
            insert.Parameters.Add("@evidence", System.Data.SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
            insert.Parameters.Add("@machine", System.Data.SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
            insert.Parameters.Add("@decision", System.Data.SqlDbType.NVarChar, 20).Value = decision;
            Assert.Equal(1, await insert.ExecuteNonQueryAsync());
        }

        await using (var inspect = new SqlCommand(
            "dbo.InspectSupervisorAcceptedProductionReceipt", connection))
        {
            inspect.CommandType = System.Data.CommandType.StoredProcedure;
            inspect.Parameters.Add("@ReceiptId", System.Data.SqlDbType.UniqueIdentifier).Value = receiptId;
            await using var reader = await inspect.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(receiptId, reader.GetGuid(reader.GetOrdinal("ReceiptId")));
            Assert.Equal(expectedStatus, reader.GetString(reader.GetOrdinal("InspectionStatus")));
            Assert.False(await reader.ReadAsync());
        }

        await using (var count = new SqlCommand(
            "SELECT (SELECT COUNT_BIG(*) FROM dbo.SupervisorAcceptedShiftProduction) + " +
            "(SELECT COUNT_BIG(*) FROM dbo.SupervisorAcceptedShiftProductionSource)", connection))
        {
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
        }
    }

}
