using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerMigration006VerifierCompatibilityIntegrationTests
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ExactPost005IsPendingThenMigration006BecomesVerifierCompatibleAndExactCurrent()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();

        var catalog = SqlMigrationCatalog.Load();
        await CreateExactPrefixAsync(connection, catalog, prefixLength: 5);

        var verifier = SqlServerRuntimeSchemaCompatibilityVerifier.CreateDefault();
        var before = await verifier.VerifyAsync(
            connection,
            LockTimeout,
            CancellationToken.None);

        Assert.Equal(SqlRuntimeCompatibilityClassification.MigrationPending, before.Classification);
        Assert.False(before.IsCompatible);
        Assert.Equal(5, await CountHistoryRowsAsync(connection));
        Assert.Equal(
            SqlRuntimeMigrationHistoryClassification.ExactPrefixPending,
            await ReadHistoryClassificationAsync(connection, catalog));

        var engine = new SqlServerMigrationEngine(
            catalog,
            new FixedUtcClock(new DateTimeOffset(2026, 9, 8, 7, 0, 0, TimeSpan.Zero)));
        await engine.ApplyAsync(connection, LockTimeout, CancellationToken.None);

        var after = await verifier.VerifyAsync(
            connection,
            LockTimeout,
            CancellationToken.None);

        Assert.Equal(SqlRuntimeCompatibilityClassification.Compatible, after.Classification);
        Assert.True(after.IsCompatible);
        Assert.Equal(6, await CountHistoryRowsAsync(connection));
        Assert.Equal(
            SqlRuntimeMigrationHistoryClassification.ExactCurrent,
            await ReadHistoryClassificationAsync(connection, catalog));
    }

    private static async Task CreateExactPrefixAsync(
        SqlConnection connection,
        SqlMigrationCatalog catalog,
        int prefixLength)
    {
        Assert.InRange(prefixLength, 0, catalog.Migrations.Length);
        await using var transaction = connection.BeginTransaction();
        await SqlServerMigrationLedgerCreator.CreateAsync(
            connection,
            transaction,
            CancellationToken.None);
        var historyStore = new SqlServerMigrationHistoryStore(
            new FixedUtcClock(new DateTimeOffset(2026, 9, 8, 4, 0, 0, TimeSpan.Zero)));

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

    private static async Task<SqlRuntimeMigrationHistoryClassification> ReadHistoryClassificationAsync(
        SqlConnection connection,
        SqlMigrationCatalog catalog)
    {
        await using var transaction = connection.BeginTransaction();
        var historyStore = new SqlServerMigrationHistoryStore(
            new FixedUtcClock(DateTimeOffset.UnixEpoch));
        var history = await historyStore.ReadAsync(
            connection,
            transaction,
            CancellationToken.None);
        var classification = SqlRuntimeMigrationHistoryClassifier.Classify(history, catalog);
        await transaction.RollbackAsync();
        return classification;
    }

    private static async Task<int> CountHistoryRowsAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dbo.FactoryConnectMigrationHistory;";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class FixedUtcClock : ISqlMigrationUtcClock
    {
        public FixedUtcClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; }
    }
}
