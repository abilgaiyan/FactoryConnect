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
        var before = await CaptureSnapshotAsync(database.ConnectionString);
        var gate = CreateGate(database.ConnectionString);

        var exception = await Assert.ThrowsAsync<SqlPersistenceStartupException>(
            async () => await gate.EnsureReadyAsync(CancellationToken.None));

        Assert.Equal(SqlPersistenceStartupFailureKind.DatabaseIncompatible, exception.FailureKind);
        Assert.NotNull(exception.CompatibilityResult);
        Assert.Equal(
            SqlRuntimeCompatibilityClassification.DatabaseUninitialized,
            exception.CompatibilityResult.Classification);

        var after = await CaptureSnapshotAsync(database.ConnectionString);
        SqlRuntimeCompatibilityPersistentStateSnapshot.AssertEquivalent(before, after);
    }

    [Fact]
    public async Task PendingMigrationIsRejectedAndPersistentStateIsUnchanged()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
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

    private static async Task<SqlRuntimeCompatibilityPersistentStateSnapshot> CaptureSnapshotAsync(
        string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        return await SqlRuntimeCompatibilityPersistentStateSnapshot.CaptureAsync(
            connection,
            CancellationToken.None);
    }

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
