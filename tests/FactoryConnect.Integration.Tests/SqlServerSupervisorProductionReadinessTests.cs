using System.Data;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerSupervisorProductionReadinessTests
{
    [Fact]
    public async Task MissingEntryReturnsExplicitBlockerAndNeverAuthorizesPublication()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await SqlServerMigrationEngine.CreateDefault().ApplyAsync(
            connection, TimeSpan.FromMinutes(2), CancellationToken.None);

        await using var command = new SqlCommand("dbo.InspectSupervisorProductionEntryReadiness", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.Add("@EntryId", SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("EntryNotFound", reader.GetString(reader.GetOrdinal("ReadinessStatus")));
        Assert.False(reader.GetBoolean(reader.GetOrdinal("PublicationAuthorized")));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task LocalApprovalWithQuantitiesCannotBypassQualificationAndIndependentVerification()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await SqlServerMigrationEngine.CreateDefault().ApplyAsync(
            connection, TimeSpan.FromMinutes(2), CancellationToken.None);

        var entryId = Guid.NewGuid();
        await using (var setup = new SqlCommand("""
            INSERT dbo.SupervisorProductionEntry
                (EntryId, MachineId, ShiftScheduleAssignmentId, ShiftId,
                 ShiftStartsAtUtc, ShiftEndsAtUtc, ProductionOrder, PartNumber,
                 Operation, EnteredBy, RecordedAtUtc, GoodQuantity, RejectedQuantity)
            VALUES (@entry, @machine, N'assignment', N'shift-1',
                    '2026-11-20T02:00:00+00:00', '2026-11-20T10:30:00+00:00',
                    N'order', N'part', N'operation', N'local-test',
                    '2026-11-20T11:00:00+00:00', 10, 1);
            INSERT dbo.SupervisorProductionEntryReview
                (EntryId, DecisionId, Decision, Reason, ReviewedBy, ReviewedAtUtc)
            VALUES (@entry, @decision, N'Approved', N'local-test',
                    N'local-reviewer', '2026-11-20T11:01:00+00:00');
            """, connection))
        {
            setup.Parameters.Add("@entry", SqlDbType.UniqueIdentifier).Value = entryId;
            setup.Parameters.Add("@machine", SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
            setup.Parameters.Add("@decision", SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
            await setup.ExecuteNonQueryAsync();
        }

        await using var inspect = new SqlCommand("dbo.InspectSupervisorProductionEntryReadiness", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        inspect.Parameters.Add("@EntryId", SqlDbType.UniqueIdentifier).Value = entryId;
        await using var reader = await inspect.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Approved", reader.GetString(reader.GetOrdinal("LocalReviewDecision")));
        Assert.Equal("QualificationReceiptMissing", reader.GetString(reader.GetOrdinal("ReadinessStatus")));
        Assert.False(reader.GetBoolean(reader.GetOrdinal("PublicationAuthorized")));
    }
}
