using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerAcceptedProductionWriterTests
{
    [Fact]
    public async Task WriterFailsClosedWithoutIndependentVerification()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await SqlServerMigrationEngine.CreateDefault().ApplyAsync(
            connection, TimeSpan.FromMinutes(2), CancellationToken.None);

        await using var command = new SqlCommand(
            "dbo.PublishSupervisorAcceptedProduction", connection)
        {
            CommandType = System.Data.CommandType.StoredProcedure
        };
        command.Parameters.Add("@PublicationId", System.Data.SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
        command.Parameters.Add("@ReceiptId", System.Data.SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
        command.Parameters.Add("@VerificationId", System.Data.SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(51038, error.Number);

        await using var verify = new SqlCommand(
            "SELECT COUNT_BIG(*) FROM dbo.SupervisorAcceptedShiftProduction", connection);
        Assert.Equal(0L, (long)(await verify.ExecuteScalarAsync())!);
    }
}
