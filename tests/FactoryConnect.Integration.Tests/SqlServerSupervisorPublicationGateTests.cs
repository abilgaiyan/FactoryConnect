using System.Data;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerSupervisorPublicationGateTests
{
    [Fact]
    public async Task UnknownReceiptAndVerificationCannotPublishOrCreateProductionRows()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await SqlServerMigrationEngine.CreateDefault().ApplyAsync(
            connection, TimeSpan.FromMinutes(2), CancellationToken.None);

        await using var publish = new SqlCommand("dbo.PublishSupervisorAcceptedProduction", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        publish.Parameters.Add("@PublicationId", SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
        publish.Parameters.Add("@ReceiptId", SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
        publish.Parameters.Add("@VerificationId", SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();

        var error = await Assert.ThrowsAsync<SqlException>(
            async () => await publish.ExecuteNonQueryAsync());
        Assert.Equal(51038, error.Number);

        await using var count = new SqlCommand("""
            SELECT (SELECT COUNT_BIG(*) FROM dbo.SupervisorAcceptedShiftProduction)
                 + (SELECT COUNT_BIG(*) FROM dbo.SupervisorAcceptedShiftProductionSource)
            """, connection);
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
    }
}
