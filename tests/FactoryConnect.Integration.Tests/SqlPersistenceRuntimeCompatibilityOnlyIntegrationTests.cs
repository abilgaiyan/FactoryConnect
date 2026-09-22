using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlPersistenceRuntimeCompatibilityOnlyIntegrationTests
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ExistingEmptyDatabaseIsUninitializedAndRuntimeCreatesNoSchema()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        var before = await CaptureFactoryConnectSchemaPresenceAsync(database.ConnectionString);
        var gate = CreateGate(database.ConnectionString);

        var exception = await Assert.ThrowsAsync<SqlPersistenceStartupException>(
            async () => await gate.EnsureReadyAsync(CancellationToken.None));

        Assert.Equal(SqlPersistenceStartupFailureKind.DatabaseIncompatible, exception.FailureKind);
        Assert.NotNull(exception.CompatibilityResult);
        Assert.Equal(
            SqlRuntimeCompatibilityClassification.DatabaseUninitialized,
            exception.CompatibilityResult.Classification);

        var after = await CaptureFactoryConnectSchemaPresenceAsync(database.ConnectionString);
        Assert.Equal(before, after);
        Assert.False(after.HasMigrationLedger);
        Assert.Equal(0, after.FactoryConnectOwnedTableCount);
    }

    [Fact]
    public async Task PendingMigrationIsRejectedAndPersistentStateIsUnchanged()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await CreateSnapshotSentinelAsync(database.ConnectionString);
        await SeedPrefixAsync(database.ConnectionString, prefixLength: 1);
        var before = await CaptureSnapshotAsync(database.ConnectionString);
        var gate = CreateGate(database.ConnectionString);

        var exception = await Assert.ThrowsAsync<SqlPersistenceStartupException>(
            async () => await gate.EnsureReadyAsync(CancellationToken.None));

        Assert.Equal(SqlPersistenceStartupFailureKind.DatabaseIncompatible, exception.FailureKind);
        Assert.NotNull(exception.CompatibilityResult);
        Assert.Equal(
            SqlRuntimeCompatibilityClassification.MigrationPending,
            exception.CompatibilityResult.Classification);

        var after = await CaptureSnapshotAsync(database.ConnectionString);
        SqlRuntimeCompatibilityPersistentStateSnapshot.AssertEquivalent(before, after);
    }

    [Fact]
    public async Task CurrentDatabaseSucceedsAndPersistentStateIsUnchanged()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await CreateSnapshotSentinelAsync(database.ConnectionString);
        await SqlServerMigrationOperation.ApplyAsync(
            database.ConnectionString,
            LockTimeout,
            CancellationToken.None);
        var before = await CaptureSnapshotAsync(database.ConnectionString);
        var gate = CreateGate(database.ConnectionString);

        await gate.EnsureReadyAsync(CancellationToken.None);

        var after = await CaptureSnapshotAsync(database.ConnectionString);
        SqlRuntimeCompatibilityPersistentStateSnapshot.AssertEquivalent(before, after);
    }

    [Fact]
    public async Task MissingConfiguredDatabaseIsVerificationOperationalFailureWithExactSqlCause()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        var builder = new SqlConnectionStringBuilder(database.ConnectionString)
        {
            InitialCatalog = $"FactoryConnect_Missing_{Guid.NewGuid():N}",
            ConnectTimeout = 2,
        };
        var gate = CreateGate(builder.ConnectionString);

        var exception = await Assert.ThrowsAsync<SqlPersistenceStartupException>(
            async () => await gate.EnsureReadyAsync(CancellationToken.None));

        Assert.Equal(
            SqlPersistenceStartupFailureKind.VerificationOperationalFailure,
            exception.FailureKind);
        Assert.Null(exception.CompatibilityResult);
        Assert.IsType<SqlException>(exception.InnerException);
    }

    [Fact]
    public async Task RuntimeRejectsBehindDatabaseExplicitMigrationConvergesThenRuntimeSucceeds()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await SeedPrefixAsync(database.ConnectionString, prefixLength: 1);
        var gate = CreateGate(database.ConnectionString);

        var rejection = await Assert.ThrowsAsync<SqlPersistenceStartupException>(
            async () => await gate.EnsureReadyAsync(CancellationToken.None));
        Assert.Equal(SqlPersistenceStartupFailureKind.DatabaseIncompatible, rejection.FailureKind);
        Assert.Equal(
            SqlRuntimeCompatibilityClassification.MigrationPending,
            rejection.CompatibilityResult?.Classification);

        await SqlServerMigrationOperation.ApplyAsync(
            database.ConnectionString,
            LockTimeout,
            CancellationToken.None);

        await gate.EnsureReadyAsync(CancellationToken.None);
    }

    private static SqlServerPersistenceStartupGate CreateGate(string connectionString) =>
        new(connectionString, new SqlPersistenceStartupOptions(LockTimeout));

    private static async Task CreateSnapshotSentinelAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE dbo.D5UnrelatedSentinel
            (
                Id int NOT NULL CONSTRAINT PK_D5UnrelatedSentinel PRIMARY KEY,
                Marker int NOT NULL
            );
            INSERT INTO dbo.D5UnrelatedSentinel (Id, Marker) VALUES (1, 314159);
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<FactoryConnectSchemaPresence> CaptureFactoryConnectSchemaPresenceAsync(
        string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                CASE
                    WHEN OBJECT_ID(N'dbo.FactoryConnectMigrationHistory', N'U') IS NULL THEN CAST(0 AS bit)
                    ELSE CAST(1 AS bit)
                END,
                COUNT(*)
            FROM sys.tables
            WHERE schema_id = SCHEMA_ID(N'dbo')
              AND name IN
              (
                  N'RawObservation',
                  N'MetricInputStream',
                  N'MetricInputFact',
                  N'ProductionContextProcessor',
                  N'MachineStateActivityAuthority',
                  N'MachineStateChangeHistory',
                  N'MachineActivityPeriodHistory'
              );
            """;

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new FactoryConnectSchemaPresence(
            reader.GetBoolean(0),
            reader.GetInt32(1));
    }

    private static async Task<SqlRuntimeCompatibilityPersistentStateSnapshot> CaptureSnapshotAsync(
        string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        return await SqlRuntimeCompatibilityPersistentStateSnapshot.CaptureAsync(
            connection,
            CancellationToken.None);
    }

    private readonly record struct FactoryConnectSchemaPresence(
        bool HasMigrationLedger,
        int FactoryConnectOwnedTableCount);

    private static async Task SeedPrefixAsync(string connectionString, int prefixLength)
    {
        var catalog = SqlMigrationCatalog.Load();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await SqlServerMigrationLedgerCreator.CreateAsync(
            connection,
            transaction,
            CancellationToken.None);
        var historyStore = new SqlServerMigrationHistoryStore(new SystemSqlMigrationUtcClock());

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
}
