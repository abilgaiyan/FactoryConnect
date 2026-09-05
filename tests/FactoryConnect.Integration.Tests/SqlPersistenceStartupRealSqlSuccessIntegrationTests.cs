using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlPersistenceStartupRealSqlSuccessIntegrationTests
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(InitialDatabaseState.Empty)]
    [InlineData(InitialDatabaseState.LegacyPost004WithoutLedger)]
    [InlineData(InitialDatabaseState.CurrentWithHistory)]
    [InlineData(InitialDatabaseState.PrefixThrough001)]
    [InlineData(InitialDatabaseState.PrefixThrough002)]
    [InlineData(InitialDatabaseState.PrefixThrough003)]
    public async Task RealStartupConvergesSupportedStatesToExactCurrent(
        InitialDatabaseState initialState)
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await SeedAsync(database.ConnectionString, initialState);

        var gate = new SqlServerPersistenceStartupGate(
            database.ConnectionString,
            new SqlPersistenceStartupOptions(LockTimeout));
        var activationCount = 0;
        var gateCompleted = false;

        await gate.EnsureReadyAsync(CancellationToken.None);
        gateCompleted = true;
        activationCount++;

        Assert.True(gateCompleted);
        Assert.Equal(1, activationCount);
        await AssertExactCurrentStateAsync(database.ConnectionString);
    }

    private static async Task SeedAsync(
        string connectionString,
        InitialDatabaseState initialState)
    {
        switch (initialState)
        {
            case InitialDatabaseState.Empty:
                return;

            case InitialDatabaseState.LegacyPost004WithoutLedger:
                await SeedLegacyPost004WithoutLedgerAsync(connectionString);
                return;

            case InitialDatabaseState.CurrentWithHistory:
                await using (var connection = new SqlConnection(connectionString))
                {
                    await connection.OpenAsync();
                    await SqlServerMigrationEngine.CreateDefault().ApplyAsync(
                        connection,
                        LockTimeout,
                        CancellationToken.None);
                }
                return;

            case InitialDatabaseState.PrefixThrough001:
                await SeedPrefixAsync(connectionString, 1);
                return;

            case InitialDatabaseState.PrefixThrough002:
                await SeedPrefixAsync(connectionString, 2);
                return;

            case InitialDatabaseState.PrefixThrough003:
                await SeedPrefixAsync(connectionString, 3);
                return;

            default:
                throw new InvalidOperationException(
                    $"Unsupported E.5 successful startup state '{initialState}'.");
        }
    }

    private static async Task SeedLegacyPost004WithoutLedgerAsync(
        string connectionString)
    {
        var catalog = SqlMigrationCatalog.Load();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        foreach (var migration in catalog.Migrations)
        {
            await SqlServerMigrationExecutor.ExecuteAsync(
                connection,
                transaction,
                migration,
                CancellationToken.None);
        }

        await transaction.CommitAsync();
    }

    private static async Task SeedPrefixAsync(
        string connectionString,
        int prefixLength)
    {
        var catalog = SqlMigrationCatalog.Load();
        Assert.InRange(prefixLength, 1, catalog.Migrations.Length - 1);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        await SqlServerMigrationLedgerCreator.CreateAsync(
            connection,
            transaction,
            CancellationToken.None);

        var historyStore = new SqlServerMigrationHistoryStore(
            new SystemSqlMigrationUtcClock());

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

    private static async Task AssertExactCurrentStateAsync(string connectionString)
    {
        var catalog = SqlMigrationCatalog.Load();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        var verifier = SqlServerRuntimeSchemaCompatibilityVerifier.CreateDefault();
        var result = await verifier.VerifyAsync(
            connection,
            LockTimeout,
            CancellationToken.None);
        Assert.Equal(SqlRuntimeCompatibilityClassification.Compatible, result.Classification);
        Assert.True(result.IsCompatible);
        Assert.Empty(result.Diagnostics);

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            var history = await new SqlServerMigrationHistoryStore(
                    new SystemSqlMigrationUtcClock())
                .ReadAsync(connection, transaction, CancellationToken.None);

            Assert.Equal(catalog.Migrations.Length, history.Length);
            for (var index = 0; index < catalog.Migrations.Length; index++)
            {
                var expected = catalog.Migrations[index];
                var actual = history[index];
                Assert.Equal(expected.MigrationId, actual.MigrationId);
                Assert.Equal(expected.Name, actual.Name);
                Assert.Equal(expected.Sha256Checksum, actual.CanonicalChecksum);
            }

            var liveSchema = await new SqlServerSchemaMetadataReader()
                .ReadFactoryConnectOwnedSchemaInTransactionAsync(
                    connection,
                    transaction,
                    CancellationToken.None);
            var comparison = SqlSchemaComparator.Compare(
                SqlRepositorySchemaDescriptors.Current,
                liveSchema);
            Assert.True(
                comparison.IsExactMatch,
                string.Join(Environment.NewLine, comparison.Differences));
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    public enum InitialDatabaseState
    {
        Empty,
        LegacyPost004WithoutLedger,
        CurrentWithHistory,
        PrefixThrough001,
        PrefixThrough002,
        PrefixThrough003,
    }
}
