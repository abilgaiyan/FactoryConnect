using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

public static class SqlServerMigrationOperation
{
    public static async Task ApplyAsync(
        string connectionString,
        TimeSpan lockTimeout,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException(
                "SQL Server connection string is required.",
                nameof(connectionString));
        }

        _ = SqlMigrationLockTimeout.ToMilliseconds(lockTimeout);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await SqlServerMigrationEngine.CreateDefault().ApplyAsync(
            connection,
            lockTimeout,
            cancellationToken);
    }
}
