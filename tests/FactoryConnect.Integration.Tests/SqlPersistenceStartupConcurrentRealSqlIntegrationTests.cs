using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Collection(SqlStartupConcurrencyDefinition.CollectionName)]
[Trait("Category", "SqlServerIntegration")]
public sealed class SqlPersistenceStartupConcurrentRealSqlIntegrationTests
{
    private static readonly TimeSpan StartupLockTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task ConcurrentExplicitMigrationsFromPendingPrefixConvergeThenConcurrentRuntimeReadinessSucceeds()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await SeedPrefixAsync(database.ConnectionString, prefixLength: 2);

        var firstMigration = SqlServerMigrationOperation.ApplyAsync(
            database.ConnectionString,
            StartupLockTimeout,
            CancellationToken.None);
        var secondMigration = SqlServerMigrationOperation.ApplyAsync(
            database.ConnectionString,
            StartupLockTimeout,
            CancellationToken.None);

        await Task.WhenAll(
            firstMigration.WaitAsync(CompletionTimeout),
            secondMigration.WaitAsync(CompletionTimeout));

        var firstGate = new SqlServerPersistenceStartupGate(
            database.ConnectionString,
            new SqlPersistenceStartupOptions(StartupLockTimeout));
        var secondGate = new SqlServerPersistenceStartupGate(
            database.ConnectionString,
            new SqlPersistenceStartupOptions(StartupLockTimeout));

        await Task.WhenAll(
            firstGate.EnsureReadyAsync(CancellationToken.None).AsTask(),
            secondGate.EnsureReadyAsync(CancellationToken.None).AsTask());

        await AssertCompatibleAsync(database.ConnectionString);
    }

    private static async Task SeedPrefixAsync(string connectionString, int prefixLength)
    {
        var catalog = SqlMigrationCatalog.Load();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await SqlServerMigrationLedgerCreator.CreateAsync(connection, transaction, CancellationToken.None);
        var historyStore = new SqlServerMigrationHistoryStore(new SystemSqlMigrationUtcClock());

        for (var index = 0; index < prefixLength; index++)
        {
            var migration = catalog.Migrations[index];
            await SqlServerMigrationExecutor.ExecuteAsync(
                connection,
                transaction,
                migration,
                CancellationToken.None);
            await historyStore.InsertAsync(
                connection,
                transaction,
                migration,
                CancellationToken.None);
        }

        await transaction.CommitAsync();
    }

    private static async Task AssertCompatibleAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        var result = await SqlServerRuntimeSchemaCompatibilityVerifier.CreateDefault().VerifyAsync(
            connection,
            StartupLockTimeout,
            CancellationToken.None);

        Assert.True(result.IsCompatible);
        Assert.Equal(SqlRuntimeCompatibilityClassification.Compatible, result.Classification);
        Assert.Empty(result.Diagnostics);
    }
}
