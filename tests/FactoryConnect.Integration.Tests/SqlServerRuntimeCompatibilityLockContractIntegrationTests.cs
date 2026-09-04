using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerRuntimeCompatibilityLockContractIntegrationTests
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task InvalidUserTableAtLedgerIdentityIsLedgerSchemaInvalid()
    {
        await using var database = await IsolatedRuntimeCompatibilityDatabase.CreateAsync();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            """
            CREATE TABLE dbo.FactoryConnectMigrationHistory
            (
                MigrationId int NOT NULL
            );
            """);

        var result = await SqlServerRuntimeSchemaCompatibilityVerifier.CreateDefault().VerifyAsync(
            connection,
            LockTimeout,
            CancellationToken.None);

        Assert.Equal(SqlRuntimeCompatibilityClassification.MigrationLedgerSchemaInvalid, result.Classification);
        Assert.False(result.IsCompatible);
    }

    [Fact]
    public async Task TwoSharedRuntimeLocksCanCoexistWithoutWaiting()
    {
        await using var database = await IsolatedRuntimeCompatibilityDatabase.CreateAsync();
        await using var firstConnection = database.CreateConnection();
        await using var secondConnection = database.CreateConnection();
        await firstConnection.OpenAsync();
        await secondConnection.OpenAsync();

        await using var firstScope = await SqlServerMigrationTransactionScope.BeginSharedAsync(
            firstConnection,
            LockTimeout,
            CancellationToken.None);
        await using var secondScope = await SqlServerMigrationTransactionScope.BeginSharedAsync(
            secondConnection,
            TimeSpan.Zero,
            CancellationToken.None);

        Assert.NotNull(firstScope.Transaction.Connection);
        Assert.NotNull(secondScope.Transaction.Connection);

        await secondScope.RollbackAsync(CancellationToken.None);
        await firstScope.RollbackAsync(CancellationToken.None);
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
            var databaseName = $"FactoryConnect_FC030_D3Lock_{Guid.NewGuid():N}";
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
