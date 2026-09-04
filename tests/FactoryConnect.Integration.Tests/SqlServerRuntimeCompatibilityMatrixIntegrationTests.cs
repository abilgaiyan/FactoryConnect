using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerRuntimeCompatibilityMatrixIntegrationTests
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(D5Scenario.Compatible, SqlRuntimeCompatibilityClassification.Compatible)]
    [InlineData(D5Scenario.DatabaseUninitialized, SqlRuntimeCompatibilityClassification.DatabaseUninitialized)]
    [InlineData(D5Scenario.LegacyAdoptionRequired, SqlRuntimeCompatibilityClassification.LegacyAdoptionRequired)]
    [InlineData(D5Scenario.UnledgeredSchemaIncompatible, SqlRuntimeCompatibilityClassification.UnledgeredSchemaIncompatible)]
    [InlineData(D5Scenario.MigrationPending, SqlRuntimeCompatibilityClassification.MigrationPending)]
    [InlineData(D5Scenario.DatabaseNewerThanSupported, SqlRuntimeCompatibilityClassification.DatabaseNewerThanSupported)]
    [InlineData(D5Scenario.MigrationIdentityMismatch, SqlRuntimeCompatibilityClassification.MigrationIdentityMismatch)]
    [InlineData(D5Scenario.MigrationChecksumMismatch, SqlRuntimeCompatibilityClassification.MigrationChecksumMismatch)]
    [InlineData(D5Scenario.MigrationHistoryInvalid, SqlRuntimeCompatibilityClassification.MigrationHistoryInvalid)]
    [InlineData(D5Scenario.MigrationLedgerSchemaInvalid, SqlRuntimeCompatibilityClassification.MigrationLedgerSchemaInvalid)]
    [InlineData(D5Scenario.MigrationSchemaDrift, SqlRuntimeCompatibilityClassification.MigrationSchemaDrift)]
    public async Task RealSqlMatrixCoversEveryFinalCompatibilityClassification(
        D5Scenario scenario,
        SqlRuntimeCompatibilityClassification expectedClassification)
    {
        await using var database = await IsolatedCompatibilityMatrixDatabase.CreateAsync();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync();
        await PrepareScenarioAsync(connection, scenario);

        var result = await VerifyAsync(connection);

        Assert.Equal(expectedClassification, result.Classification);
        var expectedCompatible = expectedClassification == SqlRuntimeCompatibilityClassification.Compatible;
        Assert.Equal(expectedCompatible, result.IsCompatible);
        Assert.Equal(expectedCompatible, result.Diagnostics.IsEmpty);
    }

    [Fact]
    public async Task RuntimeVerifierWaitsForExclusiveBoundaryAndObservesCommittedSchemaState()
    {
        await using var database = await IsolatedCompatibilityMatrixDatabase.CreateAsync();
        await using var setupConnection = database.CreateConnection();
        await using var exclusiveConnection = database.CreateConnection();
        await using var verifierConnection = database.CreateConnection();
        await setupConnection.OpenAsync();
        await exclusiveConnection.OpenAsync();
        await verifierConnection.OpenAsync();
        await ApplyCurrentAsync(setupConnection);

        var verifierSessionId = await ReadSessionIdAsync(verifierConnection);
        await using var exclusiveScope = await SqlServerMigrationTransactionScope.BeginAsync(
            exclusiveConnection,
            LockTimeout,
            CancellationToken.None);
        await ExecuteAsync(
            exclusiveConnection,
            "ALTER TABLE dbo.MachineObservation ADD D5CommittedDrift int NULL;",
            exclusiveScope.Transaction);

        var verificationTask = VerifyAsync(verifierConnection);
        await AssertWaitingOnApplicationLockAsync(
            exclusiveConnection,
            exclusiveScope.Transaction,
            verifierSessionId,
            verificationTask);

        await exclusiveScope.CommitAsync(CancellationToken.None);
        var result = await verificationTask;

        Assert.Equal(SqlRuntimeCompatibilityClassification.MigrationSchemaDrift, result.Classification);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Artifact.EndsWith(":D5CommittedDrift", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MigrationEngineWaitsForSharedBoundaryThenConvergesToCompatibleState()
    {
        await using var database = await IsolatedCompatibilityMatrixDatabase.CreateAsync();
        await using var sharedConnection = database.CreateConnection();
        await using var migratorConnection = database.CreateConnection();
        await sharedConnection.OpenAsync();
        await migratorConnection.OpenAsync();

        var migratorSessionId = await ReadSessionIdAsync(migratorConnection);
        await using var sharedScope = await SqlServerMigrationTransactionScope.BeginSharedAsync(
            sharedConnection,
            LockTimeout,
            CancellationToken.None);
        var migrationTask = SqlServerMigrationEngine.CreateDefault().ApplyAsync(
            migratorConnection,
            LockTimeout,
            CancellationToken.None);

        await AssertWaitingOnApplicationLockAsync(
            sharedConnection,
            sharedScope.Transaction,
            migratorSessionId,
            migrationTask);

        await sharedScope.RollbackAsync(CancellationToken.None);
        await migrationTask;

        var result = await VerifyAsync(migratorConnection);
        Assert.Equal(SqlRuntimeCompatibilityClassification.Compatible, result.Classification);
        Assert.True(result.Diagnostics.IsEmpty);
    }

    private static async Task PrepareScenarioAsync(SqlConnection connection, D5Scenario scenario)
    {
        if (scenario == D5Scenario.DatabaseUninitialized)
        {
            return;
        }

        await ApplyCurrentAsync(connection);
        switch (scenario)
        {
            case D5Scenario.Compatible:
                return;

            case D5Scenario.LegacyAdoptionRequired:
                await ExecuteAsync(connection, "DROP TABLE dbo.FactoryConnectMigrationHistory;");
                return;

            case D5Scenario.UnledgeredSchemaIncompatible:
                await ExecuteAsync(connection, "DROP TABLE dbo.FactoryConnectMigrationHistory;");
                await ExecuteAsync(
                    connection,
                    "ALTER TABLE dbo.MachineObservation ADD D5UnledgeredUnexpected int NULL;");
                return;

            case D5Scenario.MigrationPending:
                await ExecuteAsync(
                    connection,
                    "DELETE FROM dbo.FactoryConnectMigrationHistory WHERE MigrationId = 4;");
                return;

            case D5Scenario.DatabaseNewerThanSupported:
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

            case D5Scenario.MigrationIdentityMismatch:
                await ExecuteAsync(
                    connection,
                    "UPDATE dbo.FactoryConnectMigrationHistory SET Name = N'WrongName' WHERE MigrationId = 2;");
                return;

            case D5Scenario.MigrationChecksumMismatch:
                await ExecuteAsync(
                    connection,
                    """
                    UPDATE dbo.FactoryConnectMigrationHistory
                    SET CanonicalChecksum = 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA'
                    WHERE MigrationId = 3;
                    """);
                return;

            case D5Scenario.MigrationHistoryInvalid:
                await ExecuteAsync(
                    connection,
                    "UPDATE dbo.FactoryConnectMigrationHistory SET CanonicalChecksum = LOWER(CanonicalChecksum) WHERE MigrationId = 1;");
                return;

            case D5Scenario.MigrationLedgerSchemaInvalid:
                await ExecuteAsync(
                    connection,
                    "ALTER TABLE dbo.FactoryConnectMigrationHistory ADD D5UnexpectedLedgerColumn int NULL;");
                return;

            case D5Scenario.MigrationSchemaDrift:
                await ExecuteAsync(
                    connection,
                    "ALTER TABLE dbo.MachineObservation ADD D5UnexpectedSchemaColumn int NULL;");
                return;

            case D5Scenario.DatabaseUninitialized:
            default:
                throw new InvalidOperationException($"Unsupported D.5 compatibility scenario '{scenario}'.");
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

    private static async Task<int> ReadSessionIdAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT @@SPID;";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task AssertWaitingOnApplicationLockAsync(
        SqlConnection observerConnection,
        SqlTransaction observerTransaction,
        int waitingSessionId,
        Task operation)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (await IsWaitingOnApplicationLockAsync(
                    observerConnection,
                    observerTransaction,
                    waitingSessionId))
            {
                Assert.False(operation.IsCompleted);
                return;
            }

            if (operation.IsCompleted)
            {
                throw new Xunit.Sdk.XunitException(
                    $"Session {waitingSessionId} completed before it was observed waiting on the FactoryConnect application lock.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new Xunit.Sdk.XunitException(
            $"Session {waitingSessionId} was not observed waiting on an APPLICATION lock.");
    }

    private static async Task<bool> IsWaitingOnApplicationLockAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int sessionId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM sys.dm_tran_locks
            WHERE resource_type = N'APPLICATION'
              AND resource_database_id = DB_ID()
              AND request_status = N'WAIT'
              AND request_session_id = @SessionId;
            """;
        command.Parameters.AddWithValue("@SessionId", sessionId);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private static async Task ExecuteAsync(
        SqlConnection connection,
        string sql,
        SqlTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public enum D5Scenario
    {
        Compatible,
        DatabaseUninitialized,
        LegacyAdoptionRequired,
        UnledgeredSchemaIncompatible,
        MigrationPending,
        DatabaseNewerThanSupported,
        MigrationIdentityMismatch,
        MigrationChecksumMismatch,
        MigrationHistoryInvalid,
        MigrationLedgerSchemaInvalid,
        MigrationSchemaDrift,
    }

    private sealed class IsolatedCompatibilityMatrixDatabase : IAsyncDisposable
    {
        private readonly string _adminConnectionString;
        private readonly string _databaseName;
        private bool _exists;

        private IsolatedCompatibilityMatrixDatabase(
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

        public static async Task<IsolatedCompatibilityMatrixDatabase> CreateAsync()
        {
            var sourceConnectionString = Environment.GetEnvironmentVariable(
                SqlServerTestDatabaseFixture.ConnectionStringEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(sourceConnectionString))
            {
                throw new InvalidOperationException(
                    $"{SqlServerTestDatabaseFixture.ConnectionStringEnvironmentVariable} is required for SQL Server integration tests.");
            }

            var sourceBuilder = new SqlConnectionStringBuilder(sourceConnectionString);
            var databaseName = $"FactoryConnect_FC030_D5_{Guid.NewGuid():N}";
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

            return new IsolatedCompatibilityMatrixDatabase(
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
