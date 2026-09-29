using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerMigration014UpgradeIntegrationTests
{
    [Fact]
    public async Task ExactPost013InstallsTransitionAuthorityWithoutSynthesizingClaims()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        var catalog = SqlMigrationCatalog.Load();
        Assert.Equal(14, catalog.Migrations[^1].MigrationId);

        await using (var transaction = connection.BeginTransaction())
        {
            await SqlServerMigrationLedgerCreator.CreateAsync(connection, transaction, CancellationToken.None);
            var history = new SqlServerMigrationHistoryStore(new FixedUtcClock());
            foreach (var migration in catalog.Migrations.Take(13))
            {
                await SqlServerMigrationExecutor.ExecuteAsync(
                    connection, transaction, migration, CancellationToken.None);
                await history.InsertAsync(connection, transaction, migration, CancellationToken.None);
            }

            await transaction.CommitAsync();
        }

        await AssertSchemaAsync(connection, SqlRepositorySchemaDescriptors.Post013);
        await new SqlServerMigrationEngine(catalog, new FixedUtcClock())
            .ApplyAsync(connection, TimeSpan.FromMinutes(2), CancellationToken.None);

        await AssertSchemaAsync(connection, SqlRepositorySchemaDescriptors.Current);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT_BIG(*) FROM dbo.ProductionReferenceTimePublicationTransition;";
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }

    private static async Task AssertSchemaAsync(SqlConnection connection, SqlSchemaDescriptor expected)
    {
        await using var transaction = connection.BeginTransaction();
        var actual = await new SqlServerSchemaMetadataReader()
            .ReadFactoryConnectOwnedSchemaInTransactionAsync(
                connection, transaction, CancellationToken.None);
        var comparison = SqlSchemaComparator.Compare(expected, actual);
        Assert.True(comparison.IsExactMatch,
            string.Join(Environment.NewLine, comparison.Differences));
        await transaction.RollbackAsync();
    }

    private sealed class FixedUtcClock : ISqlMigrationUtcClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 9, 29, 0, 0, 0, TimeSpan.Zero);
    }
}
