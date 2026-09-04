using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerRuntimeCompatibilityDiagnosticsIntegrationTests
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task CompatibleDatabaseHasNoDiagnosticsAndSchemaDriftPreservesComparatorOrder()
    {
        await using var database = await IsolatedDiagnosticDatabase.CreateAsync();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync();
        await ApplyCurrentAsync(connection);

        var compatible = await VerifyAsync(connection);

        Assert.Equal(SqlRuntimeCompatibilityClassification.Compatible, compatible.Classification);
        Assert.Empty(compatible.Diagnostics);

        await ExecuteAsync(
            connection,
            "ALTER TABLE dbo.MachineObservation ADD D4UnexpectedZ int NULL, D4UnexpectedA int NULL;");

        var drift = await VerifyAsync(connection);

        Assert.Equal(SqlRuntimeCompatibilityClassification.MigrationSchemaDrift, drift.Classification);
        Assert.Equal(2, drift.Diagnostics.Length);
        Assert.All(drift.Diagnostics, diagnostic =>
        {
            Assert.Equal(SqlRuntimeCompatibilityDiagnosticCode.MigrationSchemaDifference, diagnostic.Code);
            Assert.Equal(SqlRuntimeCompatibilityDecisionStage.CurrentSchemaComparison, diagnostic.Stage);
            Assert.Equal(SqlSchemaDifferenceKind.UnexpectedColumn, diagnostic.SchemaDifferenceKind);
        });
        Assert.EndsWith(":D4UnexpectedA", drift.Diagnostics[0].Artifact, StringComparison.Ordinal);
        Assert.EndsWith(":D4UnexpectedZ", drift.Diagnostics[1].Artifact, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedHistoryDiagnosticIsStableAcrossRepeatedVerification()
    {
        await using var database = await IsolatedDiagnosticDatabase.CreateAsync();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync();
        await ApplyCurrentAsync(connection);
        await ExecuteAsync(
            connection,
            "UPDATE dbo.FactoryConnectMigrationHistory SET CanonicalChecksum = LOWER(CanonicalChecksum) WHERE MigrationId = 1;");

        var first = await VerifyAsync(connection);
        var second = await VerifyAsync(connection);

        Assert.Equal(SqlRuntimeCompatibilityClassification.MigrationHistoryInvalid, first.Classification);
        var diagnostic = Assert.Single(first.Diagnostics);
        Assert.Equal(SqlRuntimeCompatibilityDiagnosticCode.MigrationHistoryChecksumInvalid, diagnostic.Code);
        Assert.Equal(SqlRuntimeCompatibilityDecisionStage.LedgerRowSemantics, diagnostic.Stage);
        Assert.Contains("MigrationId=1", diagnostic.Artifact, StringComparison.Ordinal);
        Assert.Equal(first.Diagnostics.ToArray(), second.Diagnostics.ToArray());
    }

    [Fact]
    public async Task InvalidLedgerStructureProducesStableContractDiagnostic()
    {
        await using var database = await IsolatedDiagnosticDatabase.CreateAsync();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            "CREATE TABLE dbo.FactoryConnectMigrationHistory (MigrationId int NOT NULL PRIMARY KEY);");

        var first = await VerifyAsync(connection);
        var second = await VerifyAsync(connection);

        Assert.Equal(SqlRuntimeCompatibilityClassification.MigrationLedgerSchemaInvalid, first.Classification);
        var diagnostic = Assert.Single(first.Diagnostics);
        Assert.Equal(SqlRuntimeCompatibilityDiagnosticCode.MigrationLedgerStructureInvalid, diagnostic.Code);
        Assert.Equal(SqlRuntimeCompatibilityDecisionStage.LedgerIdentityAndPhysicalShape, diagnostic.Stage);
        Assert.Equal("dbo.FactoryConnectMigrationHistory", diagnostic.Artifact);
        Assert.Contains("invalid", diagnostic.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(first.Diagnostics.ToArray(), second.Diagnostics.ToArray());
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

    private sealed class IsolatedDiagnosticDatabase : IAsyncDisposable
    {
        private readonly string _adminConnectionString;
        private readonly string _databaseName;
        private bool _exists;

        private IsolatedDiagnosticDatabase(
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

        public static async Task<IsolatedDiagnosticDatabase> CreateAsync()
        {
            var sourceConnectionString = Environment.GetEnvironmentVariable(
                SqlServerTestDatabaseFixture.ConnectionStringEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(sourceConnectionString))
            {
                throw new InvalidOperationException(
                    $"{SqlServerTestDatabaseFixture.ConnectionStringEnvironmentVariable} is required for SQL Server integration tests.");
            }

            var sourceBuilder = new SqlConnectionStringBuilder(sourceConnectionString);
            var databaseName = $"FactoryConnect_FC030_D4_{Guid.NewGuid():N}";
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

            return new IsolatedDiagnosticDatabase(
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
