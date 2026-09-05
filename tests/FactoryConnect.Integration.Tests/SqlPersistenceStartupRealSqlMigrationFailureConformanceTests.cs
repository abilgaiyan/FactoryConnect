using System.Globalization;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlPersistenceStartupRealSqlMigrationFailureConformanceTests
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task MigrationIdentityMismatchIsPreservedAtStartupBoundary()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        var migration001 = SqlMigrationCatalog.Load().Migrations[0];
        await SeedLedgerRowAsync(
            database.ConnectionString,
            migration001.MigrationId,
            migration001.Name + "_Wrong",
            migration001.Sha256Checksum);

        Exception? migrationFailure = null;
        var verificationCount = 0;
        var activationCount = 0;
        var gate = CreateGate(
            database.ConnectionString,
            exception => migrationFailure = exception,
            () => verificationCount++);

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
        Assert.Same(migrationFailure, exception.InnerException);
        Assert.IsType<SqlMigrationHistoryException>(exception.InnerException);
        Assert.Equal(0, verificationCount);
        Assert.Equal(0, activationCount);
    }

    [Fact]
    public async Task MigrationLockTimeoutObservesWaitingStartupSessionBeforeFailure()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var blocker = new SqlConnection(database.ConnectionString);
        await blocker.OpenAsync();
        await using var blockerTransaction =
            (SqlTransaction)await blocker.BeginTransactionAsync();
        await AcquireExclusiveMigrationLockAsync(blocker, blockerTransaction);

        var startupSessionId = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? migrationFailure = null;
        var verificationCount = 0;
        var activationCount = 0;
        var gate = new SqlServerPersistenceStartupGate(
            database.ConnectionString,
            new SqlPersistenceStartupOptions(LockTimeout),
            async (connectionString, lockTimeout, cancellationToken) =>
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                startupSessionId.TrySetResult(await ReadSessionIdAsync(connection, cancellationToken));

                try
                {
                    await SqlServerMigrationEngine.CreateDefault().ApplyAsync(
                        connection,
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
                        "Verification must not execute after migration lock timeout."));
            });

        async Task RunStartupAsync()
        {
            await gate.EnsureReadyAsync(CancellationToken.None);
            activationCount++;
        }

        var startupTask = RunStartupAsync();

        try
        {
            var sessionId = await startupSessionId.Task.WaitAsync(ObservationTimeout);
            await AssertWaitingApplicationLockAsync(
                database.ConnectionString,
                sessionId,
                startupTask,
                ObservationTimeout);

            var exception = await Assert.ThrowsAsync<SqlPersistenceStartupException>(
                () => startupTask.WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.Equal(
                SqlPersistenceStartupFailureKind.MigrationOperationalFailure,
                exception.FailureKind);
            Assert.Null(exception.CompatibilityResult);
            Assert.NotNull(migrationFailure);
            Assert.Same(migrationFailure, exception.InnerException);
            Assert.Equal(0, verificationCount);
            Assert.Equal(0, activationCount);
        }
        finally
        {
            await blockerTransaction.RollbackAsync();
        }
    }

    [Fact]
    public async Task Migration003FailureRestoresStructuralStateBeforeRetry()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
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

        var verificationCount = 0;
        var activationCount = 0;
        var gate = CreateGate(
            database.ConnectionString,
            _ => { },
            () => verificationCount++);

        var exception = await Assert.ThrowsAsync<SqlPersistenceStartupException>(
            async () =>
            {
                await gate.EnsureReadyAsync(CancellationToken.None);
                activationCount++;
            });

        Assert.Equal(
            SqlPersistenceStartupFailureKind.MigrationOperationalFailure,
            exception.FailureKind);
        var migrationException = Assert.IsType<MigrationExecutionException>(
            exception.InnerException);
        Assert.Equal(3, migrationException.MigrationId);
        Assert.Equal(0, verificationCount);
        Assert.Equal(0, activationCount);

        await AssertExactHistoryPrefixAsync(database.ConnectionString, prefixLength: 2);
        await AssertMigration003RollbackStructureAsync(database.ConnectionString);

        await ExecuteNonQueryAsync(
            database.ConnectionString,
            "DROP TABLE dbo.E5Migration003RollbackConflict;");

        var retryGate = new SqlServerPersistenceStartupGate(
            database.ConnectionString,
            new SqlPersistenceStartupOptions(LockTimeout));
        await retryGate.EnsureReadyAsync(CancellationToken.None);
        activationCount++;

        Assert.Equal(1, activationCount);
        await AssertCurrentCompatibleAsync(database.ConnectionString);
    }

    private static SqlServerPersistenceStartupGate CreateGate(
        string connectionString,
        Action<Exception> onMigrationFailure,
        Action onVerification) =>
        new(
            connectionString,
            new SqlPersistenceStartupOptions(LockTimeout),
            async (stageConnectionString, stageLockTimeout, cancellationToken) =>
            {
                await using var connection = new SqlConnection(stageConnectionString);
                await connection.OpenAsync(cancellationToken);
                try
                {
                    await SqlServerMigrationEngine.CreateDefault().ApplyAsync(
                        connection,
                        stageLockTimeout,
                        cancellationToken);
                }
                catch (Exception exception)
                {
                    onMigrationFailure(exception);
                    throw;
                }
            },
            (_, _, _) =>
            {
                onVerification();
                return Task.FromException<SqlRuntimeCompatibilityResult>(
                    new InvalidOperationException(
                        "Verification must not execute after migration failure."));
            });

    private static async Task<int> ReadSessionIdAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT @@SPID;";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    private static async Task AssertWaitingApplicationLockAsync(
        string connectionString,
        int sessionId,
        Task startupTask,
        TimeSpan timeout)
    {
        await using var observer = new SqlConnection(connectionString);
        await observer.OpenAsync();
        using var timeoutSource = new CancellationTokenSource(timeout);

        while (!timeoutSource.IsCancellationRequested)
        {
            await using var command = observer.CreateCommand();
            command.CommandText = """
                SELECT TOP (1)
                    request_status,
                    request_mode,
                    request_owner_type
                FROM sys.dm_tran_locks
                WHERE request_session_id = @SessionId
                  AND resource_type = N'APPLICATION'
                ORDER BY request_status DESC;
                """;
            command.Parameters.AddWithValue("@SessionId", sessionId);

            await using var reader = await command.ExecuteReaderAsync(
                timeoutSource.Token);
            if (await reader.ReadAsync(timeoutSource.Token))
            {
                var status = reader.GetString(0);
                var mode = reader.GetString(1);
                var ownerType = reader.GetString(2);
                if (string.Equals(status, "WAIT", StringComparison.Ordinal))
                {
                    Assert.Equal("X", mode);
                    Assert.Equal("TRANSACTION", ownerType);
                    Assert.False(startupTask.IsCompleted);
                    return;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), timeoutSource.Token);
        }

        throw new TimeoutException(
            $"SQL session {sessionId} was not observed waiting on the migration APPLICATION lock.");
    }

    private static async Task AssertMigration003RollbackStructureAsync(
        string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT is_disabled, is_not_trusted
                FROM sys.foreign_keys
                WHERE parent_object_id = OBJECT_ID(N'dbo.MetricInputFact')
                  AND name = N'FK_MetricInputFact_MetricInputStream';
                """;
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.False(reader.GetBoolean(0));
            Assert.False(reader.GetBoolean(1));
            Assert.False(await reader.ReadAsync());
        }

        Assert.Equal(
            0,
            await CountSqlObjectsAsync(
                connection,
                """
                SELECT COUNT(*)
                FROM sys.key_constraints
                WHERE parent_object_id = OBJECT_ID(N'dbo.MetricInputStream')
                  AND name = N'UQ_MetricInputStream_RowMachine';
                """));
        Assert.Equal(
            0,
            await CountSqlObjectsAsync(
                connection,
                """
                SELECT COUNT(*)
                FROM sys.foreign_keys
                WHERE parent_object_id = OBJECT_ID(N'dbo.MetricInputFact')
                  AND name = N'FK_MetricInputFact_StreamMachine';
                """));

        foreach (var tableName in new[]
        {
            "ProductionContextProcessor",
            "ProductionContextCheckpoint",
            "ContextualizedActivityOutput",
            "ProductionTimeEligibilityOutput",
        })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT OBJECT_ID(@ObjectName, N'U');";
            command.Parameters.AddWithValue("@ObjectName", $"dbo.{tableName}");
            Assert.Equal(DBNull.Value, await command.ExecuteScalarAsync());
        }
    }

    private static async Task<int> CountSqlObjectsAsync(
        SqlConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            CultureInfo.InvariantCulture);
    }

    private static async Task SeedLedgerRowAsync(
        string connectionString,
        int migrationId,
        string name,
        string checksum)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction =
            (SqlTransaction)await connection.BeginTransactionAsync();
        await SqlServerMigrationLedgerCreator.CreateAsync(
            connection,
            transaction,
            CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO dbo.FactoryConnectMigrationHistory
                (MigrationId, Name, CanonicalChecksum, AppliedAtUtc)
            VALUES
                (@MigrationId, @Name, @Checksum, @AppliedAtUtc);
            """;
        command.Parameters.AddWithValue("@MigrationId", migrationId);
        command.Parameters.AddWithValue("@Name", name);
        command.Parameters.AddWithValue("@Checksum", checksum);
        command.Parameters.AddWithValue("@AppliedAtUtc", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
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

    private static async Task AssertExactHistoryPrefixAsync(
        string connectionString,
        int prefixLength)
    {
        var catalog = SqlMigrationCatalog.Load();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction =
            (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            var history = await new SqlServerMigrationHistoryStore(
                    new SystemSqlMigrationUtcClock())
                .ReadAsync(connection, transaction, CancellationToken.None);
            Assert.Equal(prefixLength, history.Length);
            for (var index = 0; index < prefixLength; index++)
            {
                Assert.Equal(catalog.Migrations[index].MigrationId, history[index].MigrationId);
                Assert.Equal(catalog.Migrations[index].Name, history[index].Name);
                Assert.Equal(catalog.Migrations[index].Sha256Checksum, history[index].CanonicalChecksum);
            }
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private static async Task AssertCurrentCompatibleAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        var result = await SqlServerRuntimeSchemaCompatibilityVerifier.CreateDefault().VerifyAsync(
            connection,
            LockTimeout,
            CancellationToken.None);
        Assert.Equal(SqlRuntimeCompatibilityClassification.Compatible, result.Classification);
        Assert.True(result.IsCompatible);
    }

    private static async Task AcquireExclusiveMigrationLockAsync(
        SqlConnection connection,
        SqlTransaction transaction)
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
            await command.ExecuteScalarAsync(),
            CultureInfo.InvariantCulture);
        Assert.True(
            result >= 0,
            $"Failed to acquire E.5 migration blocker lock. Result: {result}.");
    }

    private static async Task ExecuteNonQueryAsync(
        string connectionString,
        string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
