using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerAcceptedProductionAncestryTests
{
    [Fact]
    public async Task ReceiptCannotBeConsumedByTwoPublications()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await SqlServerMigrationEngine.CreateDefault().ApplyAsync(
            connection, TimeSpan.FromMinutes(2), CancellationToken.None);

        var receipt = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var machine = Guid.NewGuid();

        await using (var setup = new SqlCommand("""
            INSERT dbo.SupervisorProductionQualificationReceipt
                (ReceiptId, EvidenceId, EvidenceKind, PopulationKey, MachineId,
                 ShiftScheduleAssignmentId, ShiftId, ShiftStartsAtUtc, ShiftEndsAtUtc,
                 QuantityUnit, ProducedQuantity, GoodQuantity, RejectedQuantity,
                 QualificationDecision, AuthorityReference, QualifiedAtUtc)
            VALUES (@receipt, @evidence, N'SupervisorRevision', N'population-a',
                    @machine, N'assignment-a', N'shift-1',
                    '2026-11-17T02:00:00+00:00', '2026-11-17T10:30:00+00:00',
                    N'pieces', 11, 10, 1, N'Qualified', N'test-only',
                    '2026-11-17T11:00:00+00:00');

            INSERT dbo.SupervisorAcceptedShiftProduction
                (PublicationId, MachineId, ShiftScheduleAssignmentId, ShiftId,
                 ShiftStartsAtUtc, ShiftEndsAtUtc, QuantityUnit,
                 AcceptedProducedQuantity, Coverage, AuthorityReference, PublishedAtUtc)
            VALUES
                (@first, @machine, N'assignment-a', N'shift-1',
                 '2026-11-17T02:00:00+00:00', '2026-11-17T10:30:00+00:00',
                 N'pieces', 11, N'Complete', N'test-only',
                 '2026-11-17T11:00:00+00:00'),
                (@second, @machine, N'assignment-b', N'shift-2',
                 '2026-11-17T10:30:00+00:00', '2026-11-17T18:30:00+00:00',
                 N'pieces', 11, N'Complete', N'test-only',
                 '2026-11-17T19:00:00+00:00');

            INSERT dbo.SupervisorAcceptedShiftProductionSource(PublicationId, ReceiptId)
            VALUES (@first, @receipt);
            """, connection))
        {
            setup.Parameters.Add("@receipt", System.Data.SqlDbType.UniqueIdentifier).Value = receipt;
            setup.Parameters.Add("@evidence", System.Data.SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
            setup.Parameters.Add("@machine", System.Data.SqlDbType.UniqueIdentifier).Value = machine;
            setup.Parameters.Add("@first", System.Data.SqlDbType.UniqueIdentifier).Value = first;
            setup.Parameters.Add("@second", System.Data.SqlDbType.UniqueIdentifier).Value = second;
            await setup.ExecuteNonQueryAsync();
        }

        await using (var inspect = new SqlCommand(
            "dbo.InspectSupervisorAcceptedProductionReceipt", connection))
        {
            inspect.CommandType = System.Data.CommandType.StoredProcedure;
            inspect.Parameters.Add("@ReceiptId", System.Data.SqlDbType.UniqueIdentifier).Value = receipt;
            await using var reader = await inspect.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("AlreadyPublished", reader.GetString(reader.GetOrdinal("InspectionStatus")));
            Assert.False(await reader.ReadAsync());
        }

        await using (var duplicate = new SqlCommand(
            "INSERT dbo.SupervisorAcceptedShiftProductionSource(PublicationId, ReceiptId) VALUES (@second, @receipt)", connection))
        {
            duplicate.Parameters.Add("@second", System.Data.SqlDbType.UniqueIdentifier).Value = second;
            duplicate.Parameters.Add("@receipt", System.Data.SqlDbType.UniqueIdentifier).Value = receipt;
            var error = await Assert.ThrowsAsync<SqlException>(
                () => duplicate.ExecuteNonQueryAsync());
            Assert.True(error.Number is 2601 or 2627, $"Unexpected SQL error number: {error.Number}");
        }

        await using var count = new SqlCommand(
            "SELECT COUNT_BIG(*) FROM dbo.SupervisorAcceptedShiftProductionSource WHERE ReceiptId = @receipt", connection);
        count.Parameters.Add("@receipt", System.Data.SqlDbType.UniqueIdentifier).Value = receipt;
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
    }
}
