using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerMigration003HardeningIntegrationTests
{
    private static readonly int[] MigrationIdsThrough002 = [1, 2];
    private static readonly int[] MigrationIdsThrough004 = [1, 2, 3, 4];

    [Fact]
    public async Task SuccessfulMigration003LeavesCallerTransactionActive()
    {
        await using var database = await IsolatedMigrationDatabase.CreateAsync();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync();
        var catalog = SqlMigrationCatalog.Load();
        await CreateExactPrefixAsync(connection, catalog, prefixLength: 2);
        var migration003 = catalog.Migrations[2];

        await using (var transaction = connection.BeginTransaction())
        {
            await SqlServerMigrationExecutor.ExecuteAsync(
                connection,
                transaction,
                migration003,
                CancellationToken.None);

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT XACT_STATE(), @@TRANCOUNT;";
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal(1, reader.GetInt32(1));
            Assert.Same(connection, transaction.Connection);

            await transaction.RollbackAsync();
        }

        Assert.True(await ForeignKeyExistsAsync(connection, "FK_MetricInputFact_MetricInputStream"));
        Assert.False(await ConstraintExistsOnTableAsync(
            connection,
            "UQ_MetricInputStream_RowMachine",
            "MetricInputStream"));
        Assert.Equal(MigrationIdsThrough002, await ReadMigrationIdsAsync(connection));
    }

    [Fact]
    public async Task FailedMigration003RollsBackPartialDdlAndRetryCompletesCurrentState()
    {
        await using var database = await IsolatedMigrationDatabase.CreateAsync();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync();
        var catalog = SqlMigrationCatalog.Load();
        await CreateExactPrefixAsync(connection, catalog, prefixLength: 2);
        await ExecuteAsync(
            connection,
            """
            CREATE TABLE dbo.C4ConstraintConflict
            (
                Id int NOT NULL,
                CONSTRAINT UQ_MetricInputStream_RowMachine UNIQUE (Id)
            );
            """);
        var engine = new SqlServerMigrationEngine(catalog, new FixedUtcClock());

        var exception = await Assert.ThrowsAsync<MigrationExecutionException>(() =>
            engine.ApplyAsync(connection, TimeSpan.FromSeconds(10), CancellationToken.None));

        Assert.Equal(3, exception.MigrationId);
        Assert.Equal("BindMetricInputFactMachine", exception.MigrationName);
        Assert.IsType<SqlException>(exception.InnerException);
        Assert.Equal(MigrationIdsThrough002, await ReadMigrationIdsAsync(connection));
        Assert.True(await ForeignKeyExistsAsync(connection, "FK_MetricInputFact_MetricInputStream"));
        Assert.False(await ConstraintExistsOnTableAsync(
            connection,
            "UQ_MetricInputStream_RowMachine",
            "MetricInputStream"));
        Assert.False(await ObjectExistsAsync(connection, "dbo.ProductionContextProcessor"));

        await ExecuteAsync(connection, "DROP TABLE dbo.C4ConstraintConflict;");
        await engine.ApplyAsync(connection, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(MigrationIdsThrough004, await ReadMigrationIdsAsync(connection));
        Assert.False(await ForeignKeyExistsAsync(connection, "FK_MetricInputFact_MetricInputStream"));
        Assert.True(await ConstraintExistsOnTableAsync(
            connection,
            "UQ_MetricInputStream_RowMachine",
            "MetricInputStream"));
        Assert.True(await ForeignKeyExistsAsync(connection, "FK_MetricInputFact_StreamMachine"));
        Assert.True(await ObjectExistsAsync(connection, "dbo.ProductionContextProcessor"));
        await AssertCurrentStateAsync(connection, catalog);
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

    private static async Task AssertCurrentStateAsync(
        SqlConnection connection,
        SqlMigrationCatalog catalog)
    {
        await using var transaction = connection.BeginTransaction();
        var ledgerReader = new SqlServerMigrationLedgerMetadataReader();
        var ledgerState = await ledgerReader.ResolveObjectAsync(
            connection,
            transaction,
            CancellationToken.None);
        Assert.Equal(SqlMigrationLedgerObjectKind.UserTable, ledgerState.Kind);
        var ledgerSchema = await ledgerReader.ReadSchemaAsync(
            connection,
            transaction,
            Assert.IsType<int>(ledgerState.ObjectId),
            CancellationToken.None);
        SqlMigrationLedgerSchemaValidator.Validate(ledgerSchema);

        var historyStore = new SqlServerMigrationHistoryStore(new FixedUtcClock());
        var history = await historyStore.ReadAsync(connection, transaction, CancellationToken.None);
        Assert.Equal(catalog.Migrations.Length, SqlMigrationHistoryPrefixValidator.ValidateExactPrefix(history, catalog));

        var schemaReader = new SqlServerSchemaMetadataReader();
        var schema = await schemaReader.ReadFactoryConnectOwnedSchemaInTransactionAsync(
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
        var values = new List<int>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetInt32(0));
        }

        return values.ToArray();
    }

    private static async Task<bool> ForeignKeyExistsAsync(SqlConnection connection, string name)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.foreign_keys WHERE name = @Name;";
        command.Parameters.AddWithValue("@Name", name);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<bool> ConstraintExistsOnTableAsync(
        SqlConnection connection,
        string constraintName,
        string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM sys.objects AS o
            INNER JOIN sys.tables AS t ON t.object_id = o.parent_object_id
            WHERE o.name = @ConstraintName
              AND t.name = @TableName;
            """;
        command.Parameters.AddWithValue("@ConstraintName", constraintName);
        command.Parameters.AddWithValue("@TableName", tableName);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<bool> ObjectExistsAsync(SqlConnection connection, string objectName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT OBJECT_ID(@ObjectName, N'U');";
        command.Parameters.AddWithValue("@ObjectName", objectName);
        var result = await command.ExecuteScalarAsync();
        return result is not null && result != DBNull.Value;
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FixedUtcClock : ISqlMigrationUtcClock
    {
        public DateTimeOffset UtcNow => new(2026, 9, 4, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class IsolatedMigrationDatabase : IAsyncDisposable
    {
        private readonly string _adminConnectionString;
        private readonly string _databaseName;
        private bool _exists;

        private IsolatedMigrationDatabase(
            string adminConnectionString,
            string databaseName,
            string connectionString)
        {
            _adminConnectionString = adminConnectionString;
            _databaseName = databaseName;
            ConnectionString = connectionString;
            _exists = true;
        }

        public string ConnectionString { get; }

        public static async Task<IsolatedMigrationDatabase> CreateAsync()
        {
            var sourceConnectionString = Environment.GetEnvironmentVariable(
                SqlServerTestDatabaseFixture.ConnectionStringEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(sourceConnectionString))
            {
                throw new InvalidOperationException(
                    $"{SqlServerTestDatabaseFixture.ConnectionStringEnvironmentVariable} is required for SQL Server integration tests.");
            }

            var sourceBuilder = new SqlConnectionStringBuilder(sourceConnectionString);
            var databaseName = $"FactoryConnect_FC030_C4_{Guid.NewGuid():N}";
            var adminBuilder = new SqlConnectionStringBuilder(sourceBuilder.ConnectionString)
            {
                InitialCatalog = "master",
            };

            await using (var adminConnection = new SqlConnection(adminBuilder.ConnectionString))
            {
                await adminConnection.OpenAsync();
                await using var command = adminConnection.CreateCommand();
                command.CommandText = $"CREATE DATABASE [{EscapeIdentifier(databaseName)}];";
                await command.ExecuteNonQueryAsync();
            }

            var databaseBuilder = new SqlConnectionStringBuilder(sourceBuilder.ConnectionString)
            {
                InitialCatalog = databaseName,
            };

            return new IsolatedMigrationDatabase(
                adminBuilder.ConnectionString,
                databaseName,
                databaseBuilder.ConnectionString);
        }

        public SqlConnection CreateConnection() => new(ConnectionString);

        public async ValueTask DisposeAsync()
        {
            if (!_exists)
            {
                return;
            }

            await using var connection = new SqlConnection(_adminConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            var escapedIdentifier = EscapeIdentifier(_databaseName);
            var escapedLiteral = EscapeLiteral(_databaseName);
            command.CommandText =
                $"IF DB_ID(N'{escapedLiteral}') IS NOT NULL " +
                "BEGIN " +
                $"ALTER DATABASE [{escapedIdentifier}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                $"DROP DATABASE [{escapedIdentifier}]; " +
                "END;";
            await command.ExecuteNonQueryAsync();
            _exists = false;
        }

        private static string EscapeIdentifier(string value) =>
            value.Replace("]", "]]", StringComparison.Ordinal);

        private static string EscapeLiteral(string value) =>
            value.Replace("'", "''", StringComparison.Ordinal);
    }
}
