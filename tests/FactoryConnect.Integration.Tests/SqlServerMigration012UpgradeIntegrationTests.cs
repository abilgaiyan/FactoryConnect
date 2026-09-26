using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerMigration012UpgradeIntegrationTests
{
    private static readonly int[] MigrationIdsThrough011 = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
    private static readonly int[] MigrationIdsThroughCurrent = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
    private static readonly string[] Migration012Tables =
    [
        "ProductionReferenceTimeRevision",
        "ProductionReferenceTimeOutcome",
        "ProductionReferenceTimeOutcomeConflict",
        "ProductionReferenceTimePublicationCut",
    ];
    private static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task ExactPost011UpgradesThrough012OnceAndMatchesCurrentSchema()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        var catalog = SqlMigrationCatalog.Load();
        Assert.Equal(12, catalog.Migrations.Length);
        Assert.Equal(12, catalog.Migrations[^1].MigrationId);

        await CreateExactPrefixAsync(connection, catalog, prefixLength: 11);

        Assert.Equal(MigrationIdsThrough011, await ReadMigrationIdsAsync(connection));
        Assert.Equal(0, await CountMigration012HistoryRowsAsync(connection));
        foreach (var table in Migration012Tables)
        {
            Assert.False(await TableExistsAsync(connection, "dbo", table));
        }

        var engine = new SqlServerMigrationEngine(catalog, new FixedUtcClock());
        await engine.ApplyAsync(connection, LockTimeout, CancellationToken.None);

        Assert.Equal(MigrationIdsThroughCurrent, await ReadMigrationIdsAsync(connection));
        Assert.Equal(1, await CountMigration012HistoryRowsAsync(connection));
        foreach (var table in Migration012Tables)
        {
            Assert.True(await TableExistsAsync(connection, "dbo", table));
        }

        await AssertCurrentSchemaExactAsync(connection);

        await engine.ApplyAsync(connection, LockTimeout, CancellationToken.None);

        Assert.Equal(MigrationIdsThroughCurrent, await ReadMigrationIdsAsync(connection));
        Assert.Equal(1, await CountMigration012HistoryRowsAsync(connection));
        await AssertCurrentSchemaExactAsync(connection);
    }

    [Fact]
    public async Task PopulatedPost011BackfillsEmptyReferenceTimeRevisionAndCuts()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        var catalog = SqlMigrationCatalog.Load();

        await CreateExactPrefixAsync(connection, catalog, prefixLength: 11);
        await SeedAggregationAuthorityAsync(connection);

        Assert.Equal(MigrationIdsThrough011, await ReadMigrationIdsAsync(connection));
        Assert.Equal(1, await CountAggregationProcessorsAsync(connection));
        Assert.Equal(2, await CountAggregationRevisionsAsync(connection));
        Assert.Equal(0, await CountMigration012HistoryRowsAsync(connection));

        var engine = new SqlServerMigrationEngine(catalog, new FixedUtcClock());
        await engine.ApplyAsync(connection, LockTimeout, CancellationToken.None);

        Assert.Equal(MigrationIdsThroughCurrent, await ReadMigrationIdsAsync(connection));
        Assert.Equal(1, await CountReferenceTimeRevisionZeroRowsAsync(connection));
        Assert.Equal(2, await CountReferenceTimeRevisionZeroCutsAsync(connection));
        Assert.Equal(0, await CountReferenceTimeOutcomesAsync(connection));
        await AssertCurrentSchemaExactAsync(connection);

        await engine.ApplyAsync(connection, LockTimeout, CancellationToken.None);

        Assert.Equal(MigrationIdsThroughCurrent, await ReadMigrationIdsAsync(connection));
        Assert.Equal(1, await CountMigration012HistoryRowsAsync(connection));
        Assert.Equal(1, await CountReferenceTimeRevisionZeroRowsAsync(connection));
        Assert.Equal(2, await CountReferenceTimeRevisionZeroCutsAsync(connection));
        Assert.Equal(0, await CountReferenceTimeOutcomesAsync(connection));
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
        var historyStore = new SqlServerMigrationHistoryStore(new FixedUtcClock());

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

    private static async Task SeedAggregationAuthorityAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @MachineId uniqueidentifier = '00112233-4455-6677-8899-AABBCCDDEEFF';

            INSERT INTO dbo.MetricInputStream
                (MachineId, StreamKeyBinary, StreamKey)
            VALUES
                (@MachineId, 0x01, N'migration-012-upgrade');

            DECLARE @StreamRowId bigint = SCOPE_IDENTITY();

            INSERT INTO dbo.MetricAggregationProcessor
                (ProcessorKeyBinary, ProcessorKey, MetricInputStreamRowId)
            VALUES
                (0x01, N'migration-012-upgrade-processor', @StreamRowId);

            DECLARE @ProcessorRowId bigint = SCOPE_IDENTITY();

            INSERT INTO dbo.MetricAggregationCheckpoint
                (MetricAggregationProcessorRowId, Position)
            VALUES
                (@ProcessorRowId, 2);

            INSERT INTO dbo.MetricAggregationRevision
                (MetricAggregationProcessorRowId, Position)
            VALUES
                (@ProcessorRowId, 1),
                (@ProcessorRowId, 2);
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertCurrentSchemaExactAsync(SqlConnection connection)
    {
        await using var transaction = connection.BeginTransaction();
        var schema = await new SqlServerSchemaMetadataReader()
            .ReadFactoryConnectOwnedSchemaInTransactionAsync(
                connection,
                transaction,
                CancellationToken.None);
        var comparison = SqlSchemaComparator.Compare(SqlRepositorySchemaDescriptors.Current, schema);
        Assert.True(comparison.IsExactMatch, string.Join(Environment.NewLine, comparison.Differences));
        await transaction.RollbackAsync();
    }

    private static async Task<int[]> ReadMigrationIdsAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MigrationId FROM dbo.FactoryConnectMigrationHistory ORDER BY MigrationId;";
        var ids = new List<int>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt32(0));
        }

        return ids.ToArray();
    }

    private static Task<int> CountMigration012HistoryRowsAsync(SqlConnection connection) =>
        ExecuteCountAsync(connection, "SELECT COUNT(*) FROM dbo.FactoryConnectMigrationHistory WHERE MigrationId = 12;");

    private static Task<int> CountAggregationProcessorsAsync(SqlConnection connection) =>
        ExecuteCountAsync(connection, "SELECT COUNT(*) FROM dbo.MetricAggregationProcessor;");

    private static Task<int> CountAggregationRevisionsAsync(SqlConnection connection) =>
        ExecuteCountAsync(connection, "SELECT COUNT(*) FROM dbo.MetricAggregationRevision;");

    private static Task<int> CountReferenceTimeRevisionZeroRowsAsync(SqlConnection connection) =>
        ExecuteCountAsync(connection, "SELECT COUNT(*) FROM dbo.ProductionReferenceTimeRevision WHERE ProductionReferenceTimeRevision = 0;");

    private static Task<int> CountReferenceTimeRevisionZeroCutsAsync(SqlConnection connection) =>
        ExecuteCountAsync(connection, "SELECT COUNT(*) FROM dbo.ProductionReferenceTimePublicationCut WHERE ProductionReferenceTimeRevision = 0;");

    private static Task<int> CountReferenceTimeOutcomesAsync(SqlConnection connection) =>
        ExecuteCountAsync(connection, "SELECT COUNT(*) FROM dbo.ProductionReferenceTimeOutcome;");

    private static async Task<int> ExecuteCountAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<bool> TableExistsAsync(
        SqlConnection connection,
        string schema,
        string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CASE WHEN OBJECT_ID(QUOTENAME(@SchemaName) + N'.' + QUOTENAME(@TableName), N'U') IS NULL
                THEN 0 ELSE 1 END;
            """;
        command.Parameters.AddWithValue("@SchemaName", schema);
        command.Parameters.AddWithValue("@TableName", table);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private sealed class FixedUtcClock : ISqlMigrationUtcClock
    {
        public DateTimeOffset UtcNow { get; } =
            new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
    }
}
