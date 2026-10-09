using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerMigration015UpgradeIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshAndPost014UpgradeInstallEmptyAuthorityAndRerunExactly(bool upgrade)
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        var catalog = SqlMigrationCatalog.Load();
        Assert.Equal(15, catalog.Migrations[^1].MigrationId);
        if (upgrade)
        {
            await using var transaction = connection.BeginTransaction();
            await SqlServerMigrationLedgerCreator.CreateAsync(connection, transaction, CancellationToken.None);
            var history = new SqlServerMigrationHistoryStore(new Clock());
            foreach (var migration in catalog.Migrations.Take(14))
            {
                await SqlServerMigrationExecutor.ExecuteAsync(connection, transaction, migration, CancellationToken.None);
                await history.InsertAsync(connection, transaction, migration, CancellationToken.None);
            }

            await transaction.CommitAsync();
            await AssertSchemaAsync(connection, SqlRepositorySchemaDescriptors.Post014);
        }

        var engine = new SqlServerMigrationEngine(catalog, new Clock());
        await engine.ApplyAsync(connection, TimeSpan.FromMinutes(2), CancellationToken.None);
        await engine.ApplyAsync(connection, TimeSpan.FromMinutes(2), CancellationToken.None);
        await AssertSchemaAsync(connection, SqlRepositorySchemaDescriptors.Post015);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT COUNT_BIG(*) FROM dbo.OperationalMetricCoverageSubject)
                + (SELECT COUNT_BIG(*) FROM dbo.OperationalMetricCoverageVersion)
                + (SELECT COUNT_BIG(*) FROM dbo.OperationalMetricCoverageHead);
            """;
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
        command.CommandText = "SELECT COUNT_BIG(*) FROM dbo.FactoryConnectMigrationHistory WHERE MigrationId = 15;";
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    private static async Task AssertSchemaAsync(SqlConnection connection, SqlSchemaDescriptor expected)
    {
        await using var transaction = connection.BeginTransaction();
        var actual = await new SqlServerSchemaMetadataReader().ReadFactoryConnectOwnedSchemaInTransactionAsync(connection, transaction, CancellationToken.None);
        var comparison = SqlSchemaComparator.Compare(expected, actual);
        Assert.True(comparison.IsExactMatch, string.Join(Environment.NewLine, comparison.Differences));
        await transaction.RollbackAsync();
    }

    private sealed class Clock : ISqlMigrationUtcClock
    {
        public DateTimeOffset UtcNow => CoverageTestData.Start;
    }
}
