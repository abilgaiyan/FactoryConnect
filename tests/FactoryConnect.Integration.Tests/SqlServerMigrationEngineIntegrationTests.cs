using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerMigrationEngineIntegrationTests
{
    [Fact]
    public async Task FreshInstallFromBlankDatabaseProducesExactCurrentState()
    {
        await using var database = await IsolatedMigrationDatabase.CreateAsync();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync();
        await ExecuteAsync(connection, "CREATE TABLE dbo.CustomerOwnedProbe (Id int NOT NULL);");
        var engine = CreateEngine();

        await engine.ApplyAsync(connection, TimeSpan.FromSeconds(10), CancellationToken.None);

        await AssertCurrentStateAsync(connection);
        Assert.True(await ObjectExistsAsync(connection, "dbo.CustomerOwnedProbe"));
    }

    [Fact]
    public async Task LegacyPost004DatabaseIsAdoptedWithoutReplayingHistoricalDdl()
    {
        await using var database = await IsolatedMigrationDatabase.CreateAsync();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync();
        var catalog = SqlMigrationCatalog.Load();
        await ApplySchemaPrefixWithoutLedgerAsync(connection, catalog, catalog.Migrations.Length);
        await ExecuteAsync(connection, "CREATE TABLE dbo.CustomerOwnedProbe (Id int NOT NULL);");
        var engine = CreateEngine();

        await engine.ApplyAsync(connection, TimeSpan.FromSeconds(10), CancellationToken.None);

        await AssertCurrentStateAsync(connection);
        Assert.True(await ObjectExistsAsync(connection, "dbo.CustomerOwnedProbe"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task ExactLedgerPrefixExecutesOnlyPendingCatalogSuffix(int prefixLength)
    {
        await using var database = await IsolatedMigrationDatabase.CreateAsync();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync();
        var catalog = SqlMigrationCatalog.Load();
        await CreateExactPrefixAsync(connection, catalog, prefixLength);
        var engine = CreateEngine();

        await engine.ApplyAsync(connection, TimeSpan.FromSeconds(10), CancellationToken.None);

        await AssertCurrentStateAsync(connection);
    }

    [Fact]
    public async Task PartialUnledgeredFactoryConnectSchemaIsRejectedWithoutMutation()
    {
        await using var database = await IsolatedMigrationDatabase.CreateAsync();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync();
        var catalog = SqlMigrationCatalog.Load();
        await ApplySchemaPrefixWithoutLedgerAsync(connection, catalog, prefixLength: 1);
        var engine = CreateEngine();

        await Assert.ThrowsAsync<UnledgeredSchemaIncompatibleException>(() =>
            engine.ApplyAsync(connection, TimeSpan.FromSeconds(10), CancellationToken.None));

        await using var transaction = connection.BeginTransaction();
        var ledgerReader = new SqlServerMigrationLedgerMetadataReader();
        var ledgerState = await ledgerReader.ResolveObjectAsync(
            connection,
            transaction,
            CancellationToken.None);
        Assert.Equal(SqlMigrationLedgerObjectKind.Absent, ledgerState.Kind);

        var schemaReader = new SqlServerSchemaMetadataReader();
        var actual = await schemaReader.ReadFactoryConnectOwnedSchemaInTransactionAsync(
            connection,
            transaction,
            CancellationToken.None);
        var expectedPrefixDatabase = SqlUnledgeredDatabaseClassifier.Classify(actual);
        Assert.Equal(
            UnledgeredDatabaseClassification.PartialOrIncompatibleLegacy,
            expectedPrefixDatabase);
        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData(HistoryCorruption.NameMismatch)]
    [InlineData(HistoryCorruption.LowercaseChecksum)]
    [InlineData(HistoryCorruption.NonUtcOffset)]
    public async Task InvalidExistingHistoryIsRejectedBeforePendingDdl(HistoryCorruption corruption)
    {
        await using var database = await IsolatedMigrationDatabase.CreateAsync();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync();
        var catalog = SqlMigrationCatalog.Load();
        await CreateExactPrefixAsync(connection, catalog, prefixLength: 1);
        await CorruptFirstHistoryRowAsync(connection, corruption);
        var engine = CreateEngine();

        await Assert.ThrowsAsync<SqlMigrationHistoryException>(() =>
            engine.ApplyAsync(connection, TimeSpan.FromSeconds(10), CancellationToken.None));

        Assert.False(await ObjectExistsAsync(connection, "dbo.MetricInputStream"));
    }

    private static SqlServerMigrationEngine CreateEngine() =>
        new(SqlMigrationCatalog.Load(), new FixedUtcClock());

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

    private static async Task ApplySchemaPrefixWithoutLedgerAsync(
        SqlConnection connection,
        SqlMigrationCatalog catalog,
        int prefixLength)
    {
        Assert.InRange(prefixLength, 0, catalog.Migrations.Length);

        await using var transaction = connection.BeginTransaction();
        for (var index = 0; index < prefixLength; index++)
        {
            await SqlServerMigrationExecutor.ExecuteAsync(
                connection,
                transaction,
                catalog.Migrations[index],
                CancellationToken.None);
        }

        await transaction.CommitAsync();
    }

    private static async Task CorruptFirstHistoryRowAsync(
        SqlConnection connection,
        HistoryCorruption corruption)
    {
        var sql = corruption switch
        {
            HistoryCorruption.NameMismatch =>
                "UPDATE dbo.FactoryConnectMigrationHistory SET Name = N'UnexpectedName' WHERE MigrationId = 1;",
            HistoryCorruption.LowercaseChecksum =>
                "UPDATE dbo.FactoryConnectMigrationHistory SET CanonicalChecksum = LOWER(CanonicalChecksum) WHERE MigrationId = 1;",
            HistoryCorruption.NonUtcOffset =>
                "UPDATE dbo.FactoryConnectMigrationHistory SET AppliedAtUtc = CAST('2026-09-04T05:30:00+05:30' AS datetimeoffset(7)) WHERE MigrationId = 1;",
            _ => throw new InvalidOperationException($"Unsupported history corruption '{corruption}'.")
        };

        await ExecuteAsync(connection, sql);
    }

    private static async Task AssertCurrentStateAsync(SqlConnection connection)
    {
        var catalog = SqlMigrationCatalog.Load();
        await using var transaction = connection.BeginTransaction();
        var ledgerReader = new SqlServerMigrationLedgerMetadataReader();
        var ledgerState = await ledgerReader.ResolveObjectAsync(
            connection,
            transaction,
            CancellationToken.None);
        Assert.Equal(SqlMigrationLedgerObjectKind.UserTable, ledgerState.Kind);

        var ledgerSnapshot = await ledgerReader.ReadSchemaAsync(
            connection,
            transaction,
            Assert.IsType<int>(ledgerState.ObjectId),
            CancellationToken.None);
        SqlMigrationLedgerSchemaValidator.Validate(ledgerSnapshot);

        var historyStore = new SqlServerMigrationHistoryStore(new FixedUtcClock());
        var history = await historyStore.ReadAsync(
            connection,
            transaction,
            CancellationToken.None);
        var prefixLength = SqlMigrationHistoryPrefixValidator.ValidateExactPrefix(history, catalog);
        Assert.Equal(catalog.Migrations.Length, prefixLength);

        var schemaReader = new SqlServerSchemaMetadataReader();
        var schema = await schemaReader.ReadFactoryConnectOwnedSchemaInTransactionAsync(
            connection,
            transaction,
            CancellationToken.None);
        var comparison = SqlSchemaComparator.Compare(SqlRepositorySchemaDescriptors.Current, schema);
        Assert.True(comparison.IsExactMatch, string.Join(Environment.NewLine, comparison.Differences));
        await transaction.RollbackAsync();
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> ObjectExistsAsync(SqlConnection connection, string objectName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT OBJECT_ID(@ObjectName, N'U');";
        command.Parameters.AddWithValue("@ObjectName", objectName);
        var result = await command.ExecuteScalarAsync();
        return result is not null && result != DBNull.Value;
    }

    private enum HistoryCorruption
    {
        NameMismatch,
        LowercaseChecksum,
        NonUtcOffset
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
            var databaseName = $"FactoryConnect_FC030_C3_{Guid.NewGuid():N}";
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
