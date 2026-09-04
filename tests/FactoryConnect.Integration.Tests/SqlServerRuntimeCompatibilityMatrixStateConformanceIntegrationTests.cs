using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerRuntimeCompatibilityMatrixStateConformanceIntegrationTests
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(SqlServerRuntimeCompatibilityMatrixIntegrationTests.D5Scenario.Compatible)]
    [InlineData(SqlServerRuntimeCompatibilityMatrixIntegrationTests.D5Scenario.DatabaseUninitialized)]
    [InlineData(SqlServerRuntimeCompatibilityMatrixIntegrationTests.D5Scenario.LegacyAdoptionRequired)]
    [InlineData(SqlServerRuntimeCompatibilityMatrixIntegrationTests.D5Scenario.UnledgeredSchemaIncompatible)]
    [InlineData(SqlServerRuntimeCompatibilityMatrixIntegrationTests.D5Scenario.MigrationPending)]
    [InlineData(SqlServerRuntimeCompatibilityMatrixIntegrationTests.D5Scenario.DatabaseNewerThanSupported)]
    [InlineData(SqlServerRuntimeCompatibilityMatrixIntegrationTests.D5Scenario.MigrationIdentityMismatch)]
    [InlineData(SqlServerRuntimeCompatibilityMatrixIntegrationTests.D5Scenario.MigrationChecksumMismatch)]
    [InlineData(SqlServerRuntimeCompatibilityMatrixIntegrationTests.D5Scenario.MigrationHistoryInvalid)]
    [InlineData(SqlServerRuntimeCompatibilityMatrixIntegrationTests.D5Scenario.MigrationLedgerSchemaInvalid)]
    [InlineData(SqlServerRuntimeCompatibilityMatrixIntegrationTests.D5Scenario.MigrationSchemaDrift)]
    public async Task EveryScenarioIsReadOnlyAndDiagnosticsAreRepeatable(
        SqlServerRuntimeCompatibilityMatrixIntegrationTests.D5Scenario scenario)
    {
        await using var database = await IsolatedStateConformanceDatabase.CreateAsync();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync();
        await PrepareScenarioAsync(connection, scenario);

        var before = await SqlRuntimeCompatibilityPersistentStateSnapshot.CaptureAsync(
            connection,
            CancellationToken.None);

        var first = await VerifyAsync(connection);
        var afterFirst = await SqlRuntimeCompatibilityPersistentStateSnapshot.CaptureAsync(
            connection,
            CancellationToken.None);

        var second = await VerifyAsync(connection);
        var afterSecond = await SqlRuntimeCompatibilityPersistentStateSnapshot.CaptureAsync(
            connection,
            CancellationToken.None);

        SqlRuntimeCompatibilityPersistentStateSnapshot.AssertEquivalent(before, afterFirst);
        SqlRuntimeCompatibilityPersistentStateSnapshot.AssertEquivalent(before, afterSecond);

        Assert.Equal(first.Classification, second.Classification);
        Assert.Equal(first.IsCompatible, second.IsCompatible);
        Assert.Equal<SqlRuntimeCompatibilityDiagnostic>(first.Diagnostics, second.Diagnostics);
    }

    private static async Task PrepareScenarioAsync(
        SqlConnection connection,
        SqlServerRuntimeCompatibilityMatrixIntegrationTests.D5Scenario scenario)
    {
        await ExecuteAsync(
            connection,
            """
            CREATE TABLE dbo.D5UnrelatedSentinel
            (
                Id int NOT NULL CONSTRAINT PK_D5UnrelatedSentinel PRIMARY KEY,
                Marker int NOT NULL
            );
            INSERT INTO dbo.D5UnrelatedSentinel (Id, Marker) VALUES (1, 314159);
            """);

        if (scenario == SqlServerRuntimeCompatibilityMatrixIntegrationTests.D5Scenario.DatabaseUninitialized)
        {
            return;
        }

        await ApplyCurrentAsync(connection);
        switch (scenario)
        {
            case SqlServerRuntimeCompatibilityMatrixIntegrationTests.D5Scenario.Compatible:
                return;

            case SqlServerRuntimeCompatibilityMatrixIntegrationTests.D5Scenario.LegacyAdoptionRequired:
                await ExecuteAsync(connection, "DROP TABLE dbo.FactoryConnectMigrationHistory;");
                return;

            case SqlServerRuntimeCompatibilityMatrixIntegrationTests.D5Scenario.UnledgeredSchemaIncompatible:
                await ExecuteAsync(connection, "DROP TABLE dbo.FactoryConnectMigrationHistory;");
                await ExecuteAsync(
                    connection,
                    "ALTER TABLE dbo.MachineObservation ADD D5UnledgeredUnexpected int NULL;");
                return;

            case SqlServerRuntimeCompatibilityMatrixIntegrationTests.D5Scenario.MigrationPending:
                await ExecuteAsync(
                    connection,
                    "DELETE FROM dbo.FactoryConnectMigrationHistory WHERE MigrationId = 4;");
                return;

            case SqlServerRuntimeCompatibilityMatrixIntegrationTests.D5Scenario.DatabaseNewerThanSupported:
                await ExecuteAsync(
                    connection,
                    """
                    INSERT INTO dbo.FactoryConnectMigrationHistory
                        (MigrationId, Name, CanonicalChecksum, AppliedAtUtc)
                    VALUES
                        (5, N'SyntheticFutureMigration',
                         'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA',
                         '2026-09-04T12:00:00+00:00');
                    """);
                return;

            case SqlServerRuntimeCompatibilityMatrixIntegrationTests.D5Scenario.MigrationIdentityMismatch:
                await ExecuteAsync(
                    connection,
                    "UPDATE dbo.FactoryConnectMigrationHistory SET Name = N'WrongName' WHERE MigrationId = 2;");
                return;

            case SqlServerRuntimeCompatibilityMatrixIntegrationTests.D5Scenario.MigrationChecksumMismatch:
                await ExecuteAsync(
                    connection,
                    """
                    UPDATE dbo.FactoryConnectMigrationHistory
                    SET CanonicalChecksum = 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA'
                    WHERE MigrationId = 3;
                    """);
                return;

            case SqlServerRuntimeCompatibilityMatrixIntegrationTests.D5Scenario.MigrationHistoryInvalid:
                await ExecuteAsync(
                    connection,
                    "UPDATE dbo.FactoryConnectMigrationHistory SET CanonicalChecksum = LOWER(CanonicalChecksum) WHERE MigrationId = 1;");
                return;

            case SqlServerRuntimeCompatibilityMatrixIntegrationTests.D5Scenario.MigrationLedgerSchemaInvalid:
                await ExecuteAsync(
                    connection,
                    "ALTER TABLE dbo.FactoryConnectMigrationHistory ADD D5UnexpectedLedgerColumn int NULL;");
                return;

            case SqlServerRuntimeCompatibilityMatrixIntegrationTests.D5Scenario.MigrationSchemaDrift:
                await ExecuteAsync(
                    connection,
                    "ALTER TABLE dbo.MachineObservation ADD D5UnexpectedSchemaColumn int NULL;");
                return;

            case SqlServerRuntimeCompatibilityMatrixIntegrationTests.D5Scenario.DatabaseUninitialized:
            default:
                throw new InvalidOperationException($"Unsupported D.5 state-conformance scenario '{scenario}'.");
        }
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

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class IsolatedStateConformanceDatabase : IAsyncDisposable
    {
        private readonly string _adminConnectionString;
        private readonly string _databaseName;
        private bool _exists;

        private IsolatedStateConformanceDatabase(
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

        public static async Task<IsolatedStateConformanceDatabase> CreateAsync()
        {
            var sourceConnectionString = Environment.GetEnvironmentVariable(
                SqlServerTestDatabaseFixture.ConnectionStringEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(sourceConnectionString))
            {
                throw new InvalidOperationException(
                    $"{SqlServerTestDatabaseFixture.ConnectionStringEnvironmentVariable} is required for SQL Server integration tests.");
            }

            var sourceBuilder = new SqlConnectionStringBuilder(sourceConnectionString);
            var databaseName = $"FactoryConnect_FC030_D5State_{Guid.NewGuid():N}";
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

            return new IsolatedStateConformanceDatabase(
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
