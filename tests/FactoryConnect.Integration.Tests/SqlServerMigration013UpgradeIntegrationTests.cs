using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerMigration013UpgradeIntegrationTests
{
    private static readonly int[] MigrationIdsThrough012 = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
    private static readonly int[] MigrationIdsThrough013 = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13];
    private static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task ExactPost012UpgradesThrough013OnceAndMatchesCurrentSchema()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        var catalog = SqlMigrationCatalog.Load();
        Assert.Equal(13, catalog.Migrations.Length);
        Assert.Equal(13, catalog.Migrations[^1].MigrationId);

        await CreateExactPost012Async(connection, catalog);
        Assert.Equal(MigrationIdsThrough012, await ReadMigrationIdsAsync(connection));
        Assert.False(await TableExistsAsync(connection, "ProductionStandardAuthorityRevision"));
        Assert.False(await TableExistsAsync(connection, "ProductionStandardVersion"));
        await AssertSchemaExactAsync(connection, SqlRepositorySchemaDescriptors.Post012);

        var engine = new SqlServerMigrationEngine(catalog, new FixedUtcClock());
        await engine.ApplyAsync(connection, LockTimeout, CancellationToken.None);

        Assert.Equal(MigrationIdsThrough013, await ReadMigrationIdsAsync(connection));
        Assert.Equal(1, await CountAsync(connection,
            "SELECT COUNT(*) FROM dbo.FactoryConnectMigrationHistory WHERE MigrationId = 13;"));
        Assert.True(await TableExistsAsync(connection, "ProductionStandardAuthorityRevision"));
        Assert.True(await TableExistsAsync(connection, "ProductionStandardVersion"));
        Assert.Equal(1, await CountAsync(connection,
            "SELECT COUNT(*) FROM dbo.ProductionStandardAuthorityRevision WHERE ProductionStandardAuthorityRevision = 0;"));
        Assert.Equal(0, await CountAsync(connection,
            "SELECT COUNT(*) FROM dbo.ProductionStandardVersion;"));
        await AssertSchemaExactAsync(connection, SqlRepositorySchemaDescriptors.Current);

        await engine.ApplyAsync(connection, LockTimeout, CancellationToken.None);

        Assert.Equal(MigrationIdsThrough013, await ReadMigrationIdsAsync(connection));
        Assert.Equal(1, await CountAsync(connection,
            "SELECT COUNT(*) FROM dbo.FactoryConnectMigrationHistory WHERE MigrationId = 13;"));
        Assert.Equal(1, await CountAsync(connection,
            "SELECT COUNT(*) FROM dbo.ProductionStandardAuthorityRevision WHERE ProductionStandardAuthorityRevision = 0;"));
        Assert.Equal(0, await CountAsync(connection,
            "SELECT COUNT(*) FROM dbo.ProductionStandardVersion;"));
        await AssertSchemaExactAsync(connection, SqlRepositorySchemaDescriptors.Current);
    }

    private static async Task CreateExactPost012Async(SqlConnection connection, SqlMigrationCatalog catalog)
    {
        await using var transaction = connection.BeginTransaction();
        await SqlServerMigrationLedgerCreator.CreateAsync(
            connection, transaction, CancellationToken.None);
        var history = new SqlServerMigrationHistoryStore(new FixedUtcClock());
        foreach (var migration in catalog.Migrations.Take(12))
        {
            await SqlServerMigrationExecutor.ExecuteAsync(
                connection, transaction, migration, CancellationToken.None);
            await history.InsertAsync(connection, transaction, migration, CancellationToken.None);
        }

        await transaction.CommitAsync();
    }

    private static async Task AssertSchemaExactAsync(SqlConnection connection, SqlSchemaDescriptor expected)
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

    private static async Task<int[]> ReadMigrationIdsAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MigrationId FROM dbo.FactoryConnectMigrationHistory ORDER BY MigrationId;";
        var values = new List<int>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetInt32(0));
        }

        return values.ToArray();
    }

    private static async Task<bool> TableExistsAsync(SqlConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CASE WHEN OBJECT_ID(N'dbo.' + QUOTENAME(@TableName), N'U') IS NULL THEN 0 ELSE 1 END;";
        command.Parameters.AddWithValue("@TableName", tableName);
        return (int)(await command.ExecuteScalarAsync())! == 1;
    }

    private static async Task<int> CountAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private sealed class FixedUtcClock : ISqlMigrationUtcClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
    }
}
