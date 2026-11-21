using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerAcceptedProductionAtomicityTests
{
    [Fact]
    public async Task DuplicateReceiptFailureRollsBackPublicationAndPreservesExistingAncestry()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await SqlServerMigrationEngine.CreateDefault().ApplyAsync(
            connection, TimeSpan.FromMinutes(2), CancellationToken.None);

        var machine = Guid.NewGuid();
        var receipt = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await using (var seed = new SqlCommand("""
            INSERT dbo.SupervisorProductionQualificationReceipt
                (ReceiptId, EvidenceId, EvidenceKind, PopulationKey, MachineId,
                 ShiftScheduleAssignmentId, ShiftId, ShiftStartsAtUtc, ShiftEndsAtUtc,
                 QuantityUnit, ProducedQuantity, GoodQuantity, RejectedQuantity,
                 QualificationDecision, AuthorityReference, QualifiedAtUtc)
            VALUES (@receipt, @evidence, N'CncCounter', N'population-one',
                    @machine, N'assignment-one', N'shift-1',
                    '2026-11-17T02:00:00+00:00', '2026-11-17T10:30:00+00:00',
                    N'pieces', 11, 10, 1, N'Qualified', N'test-only',
                    '2026-11-17T11:00:00+00:00');
            INSERT dbo.SupervisorAcceptedShiftProduction
                (PublicationId, MachineId, ShiftScheduleAssignmentId, ShiftId,
                 ShiftStartsAtUtc, ShiftEndsAtUtc, QuantityUnit,
                 AcceptedProducedQuantity, Coverage, AuthorityReference, PublishedAtUtc)
            VALUES (@first, @machine, N'assignment-one', N'shift-1',
                    '2026-11-17T02:00:00+00:00', '2026-11-17T10:30:00+00:00',
                    N'pieces', 11, N'Complete', N'test-only',
                    '2026-11-17T11:00:00+00:00');
            INSERT dbo.SupervisorAcceptedShiftProductionSource(PublicationId, ReceiptId)
            VALUES (@first, @receipt);
            """, connection))
        {
            seed.Parameters.Add("@machine", System.Data.SqlDbType.UniqueIdentifier).Value = machine;
            seed.Parameters.Add("@receipt", System.Data.SqlDbType.UniqueIdentifier).Value = receipt;
            seed.Parameters.Add("@evidence", System.Data.SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
            seed.Parameters.Add("@first", System.Data.SqlDbType.UniqueIdentifier).Value = first;
            await seed.ExecuteNonQueryAsync();
        }

        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync())
        {
            try
            {
                await using var publish = new SqlCommand("""
                    INSERT dbo.SupervisorAcceptedShiftProduction
                        (PublicationId, MachineId, ShiftScheduleAssignmentId, ShiftId,
                         ShiftStartsAtUtc, ShiftEndsAtUtc, QuantityUnit,
                         AcceptedProducedQuantity, Coverage, AuthorityReference, PublishedAtUtc)
                    VALUES (@second, @machine, N'assignment-two', N'shift-2',
                            '2026-11-17T10:30:00+00:00', '2026-11-17T18:30:00+00:00',
                            N'pieces', 11, N'Complete', N'test-only',
                            '2026-11-17T19:00:00+00:00');
                    INSERT dbo.SupervisorAcceptedShiftProductionSource(PublicationId, ReceiptId)
                    VALUES (@second, @receipt);
                    """, connection, transaction);
                publish.Parameters.Add("@second", System.Data.SqlDbType.UniqueIdentifier).Value = second;
                publish.Parameters.Add("@machine", System.Data.SqlDbType.UniqueIdentifier).Value = machine;
                publish.Parameters.Add("@receipt", System.Data.SqlDbType.UniqueIdentifier).Value = receipt;
                var error = await Assert.ThrowsAsync<SqlException>(
                    () => publish.ExecuteNonQueryAsync());
                Assert.True(error.Number is 2601 or 2627, $"Unexpected SQL error: {error.Number}");
            }
            finally
            {
                await transaction.RollbackAsync();
            }
        }

        await using var verify = new SqlCommand("""
            SELECT
                (SELECT COUNT_BIG(*) FROM dbo.SupervisorAcceptedShiftProduction WHERE PublicationId = @second),
                (SELECT COUNT_BIG(*) FROM dbo.SupervisorAcceptedShiftProductionSource WHERE ReceiptId = @receipt),
                (SELECT COUNT_BIG(*) FROM dbo.SupervisorAcceptedShiftProduction WHERE PublicationId = @first)
            """, connection);
        verify.Parameters.Add("@second", System.Data.SqlDbType.UniqueIdentifier).Value = second;
        verify.Parameters.Add("@receipt", System.Data.SqlDbType.UniqueIdentifier).Value = receipt;
        verify.Parameters.Add("@first", System.Data.SqlDbType.UniqueIdentifier).Value = first;
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.Equal(1L, reader.GetInt64(2));
    }
}
