using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

// Only for a newly-created isolated test database. Does not use the current
// engine with an older catalog, or weaken its final Current-schema validation.
internal static class SqlMigration020TestBaseline
{
    public static async Task ApplyAsync(SqlConnection connection)
    {
        await using var transaction = connection.BeginTransaction();
        await SqlServerMigrationLedgerCreator.CreateAsync(connection, transaction, CancellationToken.None);
        var history = new SqlServerMigrationHistoryStore(new Clock());
        foreach (var migration in SqlMigrationCatalog.Load().Migrations.Take(20))
        {
            await SqlServerMigrationExecutor.ExecuteAsync(connection, transaction, migration, CancellationToken.None);
            await history.InsertAsync(connection, transaction, migration, CancellationToken.None);
        }
        await transaction.CommitAsync();
    }
    private sealed class Clock : ISqlMigrationUtcClock
    {
        public DateTimeOffset UtcNow => new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
    }
}
