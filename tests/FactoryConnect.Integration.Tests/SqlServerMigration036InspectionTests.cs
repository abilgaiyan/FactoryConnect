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
}
