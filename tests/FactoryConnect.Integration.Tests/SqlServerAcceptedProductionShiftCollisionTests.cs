using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerAcceptedProductionShiftCollisionTests
{
    [Fact]
    public async Task DistinctPopulationForPreviouslyPublishedShiftRequiresReconciliation()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await SqlServerMigrationEngine.CreateDefault().ApplyAsync(
            connection, TimeSpan.FromMinutes(2), CancellationToken.None);

        var machine = Guid.NewGuid();
        var publishedReceipt = Guid.NewGuid();
        var candidateReceipt = Guid.NewGuid();
        var publication = Guid.NewGuid();

        await using (var setup = new SqlCommand("""
            INSERT dbo.SupervisorProductionQualificationReceipt
                (ReceiptId, EvidenceId, EvidenceKind, PopulationKey, MachineId,
                 ShiftScheduleAssignmentId, ShiftId, ShiftStartsAtUtc, ShiftEndsAtUtc,
                 QuantityUnit, ProducedQuantity, GoodQuantity, RejectedQuantity,
                 QualificationDecision, AuthorityReference, QualifiedAtUtc)
            VALUES
                (@publishedReceipt, @publishedEvidence, N'CncCounter', N'population-shared',
                 @machine, N'assignment-a', N'shift-1',
                 '2026-11-17T02:00:00+00:00', '2026-11-17T10:30:00+00:00',
                 N'pieces', 11, 10, 1, N'Qualified', N'test-only',
                 '2026-11-17T11:00:00+00:00'),
                (@candidateReceipt, @candidateEvidence, N'SupervisorRevision', N'population-different',
                 @machine, N'assignment-a', N'shift-1',
                 '2026-11-17T02:00:00+00:00', '2026-11-17T10:30:00+00:00',
                 N'pieces', 11, 10, 1, N'Qualified', N'test-only',
                 '2026-11-17T19:00:00+00:00');

            INSERT dbo.SupervisorAcceptedShiftProduction
                (PublicationId, MachineId, ShiftScheduleAssignmentId, ShiftId,
                 ShiftStartsAtUtc, ShiftEndsAtUtc, QuantityUnit,
                 AcceptedProducedQuantity, Coverage, AuthorityReference, PublishedAtUtc)
            VALUES
                (@publication, @machine, N'assignment-a', N'shift-1',
                 '2026-11-17T02:00:00+00:00', '2026-11-17T10:30:00+00:00',
                 N'pieces', 11, N'Complete', N'test-only',
                 '2026-11-17T11:00:00+00:00');

            INSERT dbo.SupervisorAcceptedShiftProductionSource(PublicationId, ReceiptId)
            VALUES (@publication, @publishedReceipt);
            """, connection))
        {
            setup.Parameters.Add("@machine", System.Data.SqlDbType.UniqueIdentifier).Value = machine;
            setup.Parameters.Add("@publishedReceipt", System.Data.SqlDbType.UniqueIdentifier).Value = publishedReceipt;
            setup.Parameters.Add("@candidateReceipt", System.Data.SqlDbType.UniqueIdentifier).Value = candidateReceipt;
            setup.Parameters.Add("@publishedEvidence", System.Data.SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
            setup.Parameters.Add("@candidateEvidence", System.Data.SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
            setup.Parameters.Add("@publication", System.Data.SqlDbType.UniqueIdentifier).Value = publication;
            await setup.ExecuteNonQueryAsync();
        }

        await using (var inspect = new SqlCommand(
            "dbo.InspectSupervisorAcceptedProductionReceipt", connection))
        {
            inspect.CommandType = System.Data.CommandType.StoredProcedure;
            inspect.Parameters.Add("@ReceiptId", System.Data.SqlDbType.UniqueIdentifier).Value = candidateReceipt;
            await using var reader = await inspect.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("ShiftAlreadyPublished", reader.GetString(reader.GetOrdinal("InspectionStatus")));
            Assert.False(await reader.ReadAsync());
        }

        await using (var count = new SqlCommand(
            "SELECT COUNT_BIG(*) FROM dbo.SupervisorAcceptedShiftProductionSource", connection))
        {
            Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
        }
    }
}
