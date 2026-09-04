using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerRuntimeSchemaCompatibilityVerifierIntegrationTests
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task UninitializedDatabaseIsReportedWithoutCreatingLedgerOrSchema()
    {
        await using var database = await IsolatedRuntimeCompatibilityDatabase.CreateAsync();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync();

        var result = await VerifyAsync(connection);

        Assert.Equal(SqlRuntimeCompatibilityClassification.DatabaseUninitialized, result.Classification);
        Assert.False(result.IsCompatible);
        Assert.False(await ObjectExistsAsync(connection, "dbo.FactoryConnectMigrationHistory"));
        Assert.False(await ObjectExistsAsync(connection, "dbo.MachineObservation"));
    }

    [Fact]
    public async Task ExactCurrentHistoryAndSchemaAreCompatible()
    {
        await using var database = await IsolatedRuntimeCompatibilityDatabase.CreateAsync();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync();
        await ApplyCurrentAsync(connection);

        var result = await VerifyAsync(connection);

        Assert.Equal(SqlRuntimeCompatibilityClassification.Compatible, result.Classification);
        Assert.True(result.IsCompatible);
    }

    [Fact]
    public async Task ExactHistoryWithLiveSchemaDriftIsNotCompatible()
    {
        await using var database = await IsolatedRuntimeCompatibilityDatabase.CreateAsync();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync();
        await ApplyCurrentAsync(connection);
        await ExecuteAsync(
            connection,
            "ALTER TABLE dbo.MachineObservation ADD D3UnexpectedColumn int NULL;");

        var result = await VerifyAsync(connection);

        Assert.Equal(SqlRuntimeCompatibilityClassification.MigrationSchemaDrift, result.Classification);
        Assert.False(result.IsCompatible);
    }

    [Fact]
    public async Task ExactHistoryPrefixIsPendingAndVerifierDoesNotApplyMissingHistory()
    {
        await using var database = await IsolatedRuntimeCompatibilityDatabase.CreateAsync();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync();
        await ApplyCurrentAsync(connection);
        await ExecuteAsync(
            connection,
            "DELETE FROM dbo.FactoryConnectMigrationHistory WHERE MigrationId = 4;");

        var result = await VerifyAsync(connection);

        Assert.Equal(SqlRuntimeCompatibilityClassification.MigrationPending, result.Classification);
        Assert.False(result.IsCompatible);
        Assert.Equal(3, await CountHistoryRowsAsync(connection));
    }

    [Fact]
    public async Task MalformedPersistedHistoryIsClassifiedByRuntimeReaderRatherThanThrown()
    {
        await using var database = await IsolatedRuntimeCompatibilityDatabase.CreateAsync();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync();
        await ApplyCurrentAsync(connection);
        await ExecuteAsync(
            connection,
            "UPDATE dbo.FactoryConnectMigrationHistory SET CanonicalChecksum = LOWER(CanonicalChecksum) WHERE MigrationId = 1;");

        var result = await VerifyAsync(connection);

        Assert.Equal(SqlRuntimeCompatibilityClassification.MigrationHistoryInvalid, result.Classification);
        Assert.False(result.IsCompatible);
    }

    [Fact]
    public async Task ExactLegacyPost004SchemaWithoutLedgerRequiresAdoptionAndRemainsUnchanged()
    {
        await using var database = await IsolatedRuntimeCompatibilityDatabase.CreateAsync();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync();
        await ApplyCurrentAsync(connection);
        await ExecuteAsync(connection, "DROP TABLE dbo.FactoryConnectMigrationHistory;");

        var result = await VerifyAsync(connection);

        Assert.Equal(SqlRuntimeCompatibilityClassification.LegacyAdoptionRequired, result.Classification);
        Assert.False(result.IsCompatible);
        Assert.False(await ObjectExistsAsync(connection, "dbo.FactoryConnectMigrationHistory"));
        Assert.True(await ObjectExistsAsync(connection, "dbo.MachineObservation"));
    }

    [Fact]
    public async Task IncompatibleUnledgeredOwnedSchemaIsRejectedWithoutRepair()
    {
        await using var database = await IsolatedRuntimeCompatibilityDatabase.CreateAsync();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync();
        await ApplyCurrentAsync(connection);
        await ExecuteAsync(connection, "DROP TABLE dbo.FactoryConnectMigrationHistory;");
        await ExecuteAsync(
            connection,
            "ALTER TABLE dbo.MachineObservation ADD D3UnexpectedColumn int NULL;");

        var result = await VerifyAsync(connection);

        Assert.Equal(SqlRuntimeCompatibilityClassification.UnledgeredSchemaIncompatible, result.Classification);
        Assert.False(result.IsCompatible);
        Assert.False(await ObjectExistsAsync(connection, "dbo.FactoryConnectMigrationHistory"));
        Assert.True(await ColumnExistsAsync(connection, "dbo.MachineObservation", "D3UnexpectedColumn"));
    }

    [Fact]
    public async Task IncompatibleObjectAtLedgerIdentityIsLedgerSchemaInvalid()
    {
        await using var database = await IsolatedRuntimeCompatibilityDatabase.CreateAsync();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            "CREATE VIEW dbo.FactoryConnectMigrationHistory AS SELECT CAST(1 AS int) AS MigrationId;");

        var result = await VerifyAsync(connection);

        Assert.Equal(SqlRuntimeCompatibilityClassification.MigrationLedgerSchemaInvalid, result.Classification);
        Assert.False(result.IsCompatible);
    }

    [Fact]
    public async Task SharedRuntimeLockUsesSameResourceAndBlocksExclusiveMigrationLock()
    {
        await using var database = await IsolatedRuntimeCompatibilityDatabase.CreateAsync();
        await using var sharedConnection = database.CreateConnection();
        await using var exclusiveConnection = database.CreateConnection();
        await sharedConnection.OpenAsync();
        await exclusiveConnection.OpenAsync();

        await using var sharedScope = await SqlServerMigrationTransactionScope.BeginSharedAsync(
            sharedConnection,
            LockTimeout,
            CancellationToken.None);

        var failure = await Assert.ThrowsAsync<SqlMigrationLockAcquisitionException>(() =>
            SqlServerMigrationTransactionScope.BeginAsync(
                exclusiveConnection,
                TimeSpan.Zero,
                CancellationToken.None));

        Assert.True(failure.ReturnCode < 0);
        await sharedScope.RollbackAsync(CancellationToken.None);
    }

    private static async Task<SqlRuntimeCompatibilityResult> VerifyAsync(SqlConnection connection) =>
        await SqlServerRuntimeSchemaCompatibilityVerifier.CreateDefault().VerifyAsync(
            connection,
            LockTimeout,
            CancellationToken.None);

    private static async Task ApplyCurrentAsync(SqlConnection connection) =>
        await SqlServerMigrationEngine.CreateDefault().ApplyAsync(
            connection,
            LockTimeout,
            CancellationToken.None);

    private static async Task<int> CountHistoryRowsAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dbo.FactoryConnectMigrationHistory;";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<bool> ObjectExistsAsync(SqlConnection connection, string objectName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT OBJECT_ID(@ObjectName, N'U');";
        command.Parameters.AddWithValue("@ObjectName", objectName);
        var result = await command.ExecuteScalarAsync();
        return result is not null && result != DBNull.Value;
    }

    private static async Task<bool> ColumnExistsAsync(
        SqlConnection connection,
        string objectName,
        string columnName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COL_LENGTH(@ObjectName, @ColumnName);";
        command.Parameters.AddWithValue("@ObjectName", objectName);
        command.Parameters.AddWithValue("@ColumnName", columnName);
        var result = await command.ExecuteScalarAsync();
        return result is not null && result != DBNull.Value;
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class IsolatedRuntimeCompatibilityDatabase : IAsyncDisposable
    {
        private readonly string _adminConnectionString;
        private readonly string _databaseName;
        private bool _exists;

        private IsolatedRuntimeCompatibilityDatabase(
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

        public static async Task<IsolatedRuntimeCompatibilityDatabase> CreateAsync()
        {
            var sourceConnectionString = Environment.GetEnvironmentVariable(
                SqlServerTestDatabaseFixture.ConnectionStringEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(sourceConnectionString))
            {
                throw new InvalidOperationException(
                    $"{SqlServerTestDatabaseFixture.ConnectionStringEnvironmentVariable} is required for SQL Server integration tests.");
            }

            var sourceBuilder = new SqlConnectionStringBuilder(sourceConnectionString);
            var databaseName = $"FactoryConnect_FC030_D3_{Guid.NewGuid():N}";
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

            return new IsolatedRuntimeCompatibilityDatabase(
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
