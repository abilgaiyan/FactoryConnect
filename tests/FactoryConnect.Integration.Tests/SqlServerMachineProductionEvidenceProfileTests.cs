using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerMachineProductionEvidenceProfileTests
{
    [Fact]
    public async Task StateOnlyMachineCannotBeConfiguredWithCounterSemantics()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await SqlServerMigrationEngine.CreateDefault().ApplyAsync(
            connection, TimeSpan.FromMinutes(2), CancellationToken.None);

        await using var command = new SqlCommand("""
            INSERT dbo.MachineProductionEvidenceProfile
                (ProfileRevisionId, MachineId, RevisionNumber, EvidenceMode,
                 StateDataItemId, CounterDataItemId, CounterSemantics,
                 EffectiveFromUtc, ConfiguredBy, ConfiguredAtUtc)
            VALUES (NEWID(), NEWID(), 1, N'StateOnly',
                    N'execution', N'part_count', N'CumulativeResettable',
                    '2026-11-20T00:00:00+00:00', N'test',
                    '2026-11-20T00:00:00+00:00');
            """, connection);
        await Assert.ThrowsAsync<SqlException>(
            async () => await command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task StateOnlyProfileCanExistWithoutInventingProducedQuantity()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await SqlServerMigrationEngine.CreateDefault().ApplyAsync(
            connection, TimeSpan.FromMinutes(2), CancellationToken.None);

        await using var command = new SqlCommand("""
            INSERT dbo.MachineProductionEvidenceProfile
                (ProfileRevisionId, MachineId, RevisionNumber, EvidenceMode,
                 StateDataItemId, CounterDataItemId, CounterSemantics,
                 EffectiveFromUtc, ConfiguredBy, ConfiguredAtUtc)
            VALUES (NEWID(), NEWID(), 1, N'StateOnly',
                    N'execution', NULL, NULL,
                    '2026-11-20T00:00:00+00:00', N'test',
                    '2026-11-20T00:00:00+00:00');
            SELECT COUNT_BIG(*) FROM dbo.SupervisorAcceptedShiftProduction;
            """, connection);
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }
}
