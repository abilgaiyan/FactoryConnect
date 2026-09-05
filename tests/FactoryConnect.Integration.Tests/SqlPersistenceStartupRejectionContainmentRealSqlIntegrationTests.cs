using System.Globalization;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlPersistenceStartupRejectionContainmentRealSqlIntegrationTests
{
    private static readonly TimeSpan StartupLockTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan VerificationFailureLockTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Migration003FailurePreservesPersistentSnapshotAndPreventsActivation()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await CreateSnapshotSentinelAsync(database.ConnectionString);
        await SeedPrefixAsync(database.ConnectionString, prefixLength: 2);
        await ExecuteNonQueryAsync(
            database.ConnectionString,
            """
            CREATE TABLE dbo.E5Migration003RollbackConflict
            (
                Id int NOT NULL,
                CONSTRAINT UQ_MetricInputStream_RowMachine UNIQUE (Id)
            );
            """);

        var before = await CaptureSnapshotAsync(database.ConnectionString);
        Exception? migrationFailure = null;
        var verificationCount = 0;
        var activationCount = 0;

        var gate = new SqlServerPersistenceStartupGate(
            database.ConnectionString,
            new SqlPersistenceStartupOptions(StartupLockTimeout),
            async (connectionString, lockTimeout, cancellationToken) =>
            {
                try
                {
                    await RunRealMigrationAsync(
                        connectionString,
                        lockTimeout,
                        cancellationToken);
                }
                catch (Exception exception)
                {
                    migrationFailure = exception;
                    throw;
                }
            },
            (_, _, _) =>
            {
                verificationCount++;
                return Task.FromException<SqlRuntimeCompatibilityResult>(
                    new InvalidOperationException(
                        "Verification must not execute after the E.5.6 migration failure."));
            });

        var exception = await Assert.ThrowsAsync<SqlPersistenceStartupException>(
            async () =>
            {
                await gate.EnsureReadyAsync(CancellationToken.None);
                activationCount++;
            });

        Assert.Equal(
            SqlPersistenceStartupFailureKind.MigrationOperationalFailure,
            exception.FailureKind);
        Assert.Null(exception.CompatibilityResult);
        Assert.NotNull(migrationFailure);
        var migrationException = Assert.IsType<MigrationExecutionException>(migrationFailure);
        Assert.Equal(3, migrationException.MigrationId);
        Assert.Same(migrationFailure, exception.InnerException);
        Assert.Equal(0, verificationCount);
        Assert.Equal(0, activationCount);

        var after = await CaptureSnapshotAsync(database.ConnectionString);
        SqlRuntimeCompatibilityPersistentStateSnapshot.AssertEquivalent(before, after);
    }

    [Fact]
    public async Task DatabaseIncompatiblePreservesDriftedPersistentSnapshotAndPreventsActivation()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await CreateSnapshotSentinelAsync(database.ConnectionString);

        SqlRuntimeCompatibilityPersistentStateSnapshot? beforeVerification = null;
        SqlRuntimeCompatibilityResult? verificationResult = null;
        var activationCount = 0;

        var gate = new SqlServerPersistenceStartupGate(
            database.ConnectionString,
            new SqlPersistenceStartupOptions(StartupLockTimeout),
            async (connectionString, lockTimeout, cancellationToken) =>
            {
                await RunRealMigrationAsync(
                    connectionString,
                    lockTimeout,
                    cancellationToken);
                await ExecuteNonQueryAsync(
                    connectionString,
                    "DROP INDEX IX_MetricInputFact_OrderedRead ON dbo.MetricInputFact;",
                    cancellationToken);
            },
            async (connectionString, lockTimeout, cancellationToken) =>
            {
                beforeVerification = await CaptureSnapshotAsync(
                    connectionString,
                    cancellationToken);
                verificationResult = await RunRealVerificationAsync(
                    connectionString,
                    lockTimeout,
                    cancellationToken);
                return verificationResult;
            });

        var exception = await Assert.ThrowsAsync<SqlPersistenceStartupException>(
            async () =>
            {
                await gate.EnsureReadyAsync(CancellationToken.None);
                activationCount++;
            });

        Assert.Equal(
            SqlPersistenceStartupFailureKind.DatabaseIncompatible,
            exception.FailureKind);
        Assert.Null(exception.InnerException);
        Assert.NotNull(beforeVerification);
        Assert.NotNull(verificationResult);
        Assert.Same(verificationResult, exception.CompatibilityResult);
        Assert.Equal(
            SqlRuntimeCompatibilityClassification.MigrationSchemaDrift,
            verificationResult.Classification);
        Assert.False(verificationResult.IsCompatible);
        Assert.NotEmpty(verificationResult.Diagnostics);
        Assert.Equal(0, activationCount);

        var after = await CaptureSnapshotAsync(database.ConnectionString);
        SqlRuntimeCompatibilityPersistentStateSnapshot.AssertEquivalent(
            beforeVerification,
            after);
    }

    [Fact]
    public async Task VerificationOperationalFailurePreservesPersistentSnapshotAndPreventsActivation()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await CreateSnapshotSentinelAsync(database.ConnectionString);
        await RunRealMigrationAsync(
            database.ConnectionString,
            StartupLockTimeout,
            CancellationToken.None);

        var before = await CaptureSnapshotAsync(database.ConnectionString);
        SqlConnection? blockerConnection = null;
        SqlTransaction? blockerTransaction = null;
        Exception? verificationFailure = null;
        Exception? primaryFailure = null;
        var activationCount = 0;

        var gate = new SqlServerPersistenceStartupGate(
            database.ConnectionString,
            new SqlPersistenceStartupOptions(VerificationFailureLockTimeout),
            async (connectionString, lockTimeout, cancellationToken) =>
            {
                await RunRealMigrationAsync(
                    connectionString,
                    lockTimeout,
                    cancellationToken);
                blockerConnection = new SqlConnection(connectionString);
                await blockerConnection.OpenAsync(cancellationToken);
                blockerTransaction = (SqlTransaction)await blockerConnection.BeginTransactionAsync(
                    cancellationToken);
                await AcquireExclusiveMigrationLockAsync(
                    blockerConnection,
                    blockerTransaction,
                    cancellationToken);
            },
            async (connectionString, lockTimeout, cancellationToken) =>
            {
                try
                {
                    return await RunRealVerificationAsync(
                        connectionString,
                        lockTimeout,
                        cancellationToken);
                }
                catch (Exception exception)
                {
                    verificationFailure = exception;
                    throw;
                }
            });

        try
        {
            var exception = await Assert.ThrowsAsync<SqlPersistenceStartupException>(
                async () =>
                {
                    await gate.EnsureReadyAsync(CancellationToken.None)
                        .AsTask()
                        .WaitAsync(CompletionTimeout);
                    activationCount++;
                });
            primaryFailure = exception;

            Assert.Equal(
                SqlPersistenceStartupFailureKind.VerificationOperationalFailure,
                exception.FailureKind);
            Assert.Null(exception.CompatibilityResult);
            Assert.NotNull(verificationFailure);
            Assert.Same(verificationFailure, exception.InnerException);
            Assert.Equal(0, activationCount);
        }
        catch (Exception exception)
        {
            primaryFailure ??= exception;
            throw;
        }
        finally
        {
            await CleanupBlockerAsync(
                blockerConnection,
                blockerTransaction,
                primaryFailure);
        }

        var after = await CaptureSnapshotAsync(database.ConnectionString);
        SqlRuntimeCompatibilityPersistentStateSnapshot.AssertEquivalent(before, after);
    }

    private static async Task CreateSnapshotSentinelAsync(string connectionString) =>
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE dbo.D5UnrelatedSentinel
            (
                Id int NOT NULL CONSTRAINT PK_D5UnrelatedSentinel PRIMARY KEY,
                Marker int NOT NULL
            );
            INSERT INTO dbo.D5UnrelatedSentinel (Id, Marker) VALUES (1, 314159);
            """);

    private static async Task<SqlRuntimeCompatibilityPersistentStateSnapshot> CaptureSnapshotAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return await SqlRuntimeCompatibilityPersistentStateSnapshot.CaptureAsync(
            connection,
            cancellationToken);
    }

    private static async Task RunRealMigrationAsync(
        string connectionString,
        TimeSpan lockTimeout,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await SqlServerMigrationEngine.CreateDefault().ApplyAsync(
            connection,
            lockTimeout,
            cancellationToken);
    }

    private static async Task<SqlRuntimeCompatibilityResult> RunRealVerificationAsync(
        string connectionString,
        TimeSpan lockTimeout,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return await SqlServerRuntimeSchemaCompatibilityVerifier.CreateDefault().VerifyAsync(
            connection,
            lockTimeout,
            cancellationToken);
    }

    private static async Task SeedPrefixAsync(
        string connectionString,
        int prefixLength)
    {
        var catalog = SqlMigrationCatalog.Load();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction =
            (SqlTransaction)await connection.BeginTransactionAsync();
        await SqlServerMigrationLedgerCreator.CreateAsync(
            connection,
            transaction,
            CancellationToken.None);

        var historyStore = new SqlServerMigrationHistoryStore(
            new SystemSqlMigrationUtcClock());
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

    private static async Task AcquireExclusiveMigrationLockAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DECLARE @Result int;
            EXEC @Result = sys.sp_getapplock
                @Resource = N'FactoryConnect.SqlMigration',
                @LockMode = N'Exclusive',
                @LockOwner = N'Transaction',
                @LockTimeout = 5000,
                @DbPrincipal = N'public';
            SELECT @Result;
            """;
        var result = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
        Assert.True(
            result >= 0,
            $"Failed to acquire E.5.6 verification blocker lock. Result: {result}.");
    }

    private static async Task ExecuteNonQueryAsync(
        string connectionString,
        string sql,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CleanupBlockerAsync(
        SqlConnection? connection,
        SqlTransaction? transaction,
        Exception? primaryFailure)
    {
        List<Exception>? cleanupFailures = null;

        if (transaction is not null)
        {
            try
            {
                await transaction.RollbackAsync();
            }
            catch (Exception exception)
            {
                (cleanupFailures ??= []).Add(exception);
            }

            try
            {
                await transaction.DisposeAsync();
            }
            catch (Exception exception)
            {
                (cleanupFailures ??= []).Add(exception);
            }
        }

        if (connection is not null)
        {
            try
            {
                await connection.DisposeAsync();
            }
            catch (Exception exception)
            {
                (cleanupFailures ??= []).Add(exception);
            }
        }

        if (cleanupFailures is null)
        {
            return;
        }

        var cleanupFailure = new AggregateException(
            "E.5.6 verification blocker cleanup failed.",
            cleanupFailures);
        if (primaryFailure is not null)
        {
            primaryFailure.Data["E.5.6 CleanupFailure"] = cleanupFailure;
            return;
        }

        throw cleanupFailure;
    }
}
