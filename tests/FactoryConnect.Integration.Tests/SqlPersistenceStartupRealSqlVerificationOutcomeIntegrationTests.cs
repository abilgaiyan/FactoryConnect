using System.Globalization;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlPersistenceStartupRealSqlVerificationOutcomeIntegrationTests
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task SchemaDriftAfterRealMigrationProducesExactDatabaseIncompatibleResult()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        SqlRuntimeCompatibilityResult? verificationResult = null;
        var activationCount = 0;

        var gate = new SqlServerPersistenceStartupGate(
            database.ConnectionString,
            new SqlPersistenceStartupOptions(LockTimeout),
            async (connectionString, lockTimeout, cancellationToken) =>
            {
                await RunRealMigrationAsync(connectionString, lockTimeout, cancellationToken);
                await ExecuteNonQueryAsync(
                    connectionString,
                    "DROP INDEX IX_MetricInputFact_OrderedRead ON dbo.MetricInputFact;",
                    cancellationToken);
            },
            async (connectionString, lockTimeout, cancellationToken) =>
            {
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
        Assert.NotNull(verificationResult);
        Assert.Same(verificationResult, exception.CompatibilityResult);
        Assert.Equal(
            SqlRuntimeCompatibilityClassification.MigrationSchemaDrift,
            verificationResult.Classification);
        Assert.False(verificationResult.IsCompatible);
        Assert.NotEmpty(verificationResult.Diagnostics);
        Assert.Equal(0, activationCount);
    }

    [Fact]
    public async Task VerificationLockTimeoutObservesWaitingSharedSessionAndPreservesExactFailure()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();

        SqlConnection? blockerConnection = null;
        SqlTransaction? blockerTransaction = null;
        var blockerReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var verificationSessionId = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? verificationFailure = null;
        Exception? primaryFailure = null;
        var activationCount = 0;

        var gate = new SqlServerPersistenceStartupGate(
            database.ConnectionString,
            new SqlPersistenceStartupOptions(LockTimeout),
            async (connectionString, lockTimeout, cancellationToken) =>
            {
                await RunRealMigrationAsync(connectionString, lockTimeout, cancellationToken);
                blockerConnection = new SqlConnection(connectionString);
                await blockerConnection.OpenAsync(cancellationToken);
                blockerTransaction = (SqlTransaction)await blockerConnection.BeginTransactionAsync(
                    cancellationToken);
                await AcquireExclusiveMigrationLockAsync(
                    blockerConnection,
                    blockerTransaction,
                    cancellationToken);
                blockerReady.TrySetResult();
            },
            async (connectionString, lockTimeout, cancellationToken) =>
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                verificationSessionId.TrySetResult(
                    await ReadSessionIdAsync(connection, cancellationToken));
                try
                {
                    return await SqlServerRuntimeSchemaCompatibilityVerifier.CreateDefault().VerifyAsync(
                        connection,
                        lockTimeout,
                        cancellationToken);
                }
                catch (Exception exception)
                {
                    verificationFailure = exception;
                    throw;
                }
            });

        async Task RunStartupAsync()
        {
            await gate.EnsureReadyAsync(CancellationToken.None);
            activationCount++;
        }

        var startupTask = RunStartupAsync();

        try
        {
            await blockerReady.Task.WaitAsync(SetupTimeout);
            var sessionId = await verificationSessionId.Task.WaitAsync(ObservationTimeout);
            await AssertWaitingApplicationLockAsync(
                database.ConnectionString,
                sessionId,
                expectedMode: "S",
                startupTask,
                ObservationTimeout);

            var exception = await Assert.ThrowsAsync<SqlPersistenceStartupException>(
                () => startupTask.WaitAsync(CompletionTimeout));
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
    }

    [Fact]
    public async Task VerificationCancellationWhileWaitingPreservesProviderOperationalFailure()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        using var cancellationSource = new CancellationTokenSource();

        SqlConnection? blockerConnection = null;
        SqlTransaction? blockerTransaction = null;
        var blockerReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var verificationSessionId = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? verificationFailure = null;
        Exception? primaryFailure = null;
        var activationCount = 0;

        var gate = new SqlServerPersistenceStartupGate(
            database.ConnectionString,
            new SqlPersistenceStartupOptions(TimeSpan.FromSeconds(30)),
            async (connectionString, lockTimeout, cancellationToken) =>
            {
                await RunRealMigrationAsync(connectionString, lockTimeout, cancellationToken);
                blockerConnection = new SqlConnection(connectionString);
                await blockerConnection.OpenAsync(cancellationToken);
                blockerTransaction = (SqlTransaction)await blockerConnection.BeginTransactionAsync(
                    cancellationToken);
                await AcquireExclusiveMigrationLockAsync(
                    blockerConnection,
                    blockerTransaction,
                    cancellationToken);
                blockerReady.TrySetResult();
            },
            async (connectionString, lockTimeout, cancellationToken) =>
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                verificationSessionId.TrySetResult(
                    await ReadSessionIdAsync(connection, cancellationToken));
                try
                {
                    return await SqlServerRuntimeSchemaCompatibilityVerifier.CreateDefault().VerifyAsync(
                        connection,
                        lockTimeout,
                        cancellationToken);
                }
                catch (Exception exception)
                {
                    verificationFailure = exception;
                    throw;
                }
            });

        async Task RunStartupAsync()
        {
            await gate.EnsureReadyAsync(cancellationSource.Token);
            activationCount++;
        }

        var startupTask = RunStartupAsync();

        try
        {
            await blockerReady.Task.WaitAsync(SetupTimeout);
            var sessionId = await verificationSessionId.Task.WaitAsync(ObservationTimeout);
            await AssertWaitingApplicationLockAsync(
                database.ConnectionString,
                sessionId,
                expectedMode: "S",
                startupTask,
                ObservationTimeout);

            cancellationSource.Cancel();

            var exception = await Assert.ThrowsAsync<SqlPersistenceStartupException>(
                () => startupTask.WaitAsync(CompletionTimeout));
            primaryFailure = exception;

            Assert.Equal(
                SqlPersistenceStartupFailureKind.VerificationOperationalFailure,
                exception.FailureKind);
            Assert.Null(exception.CompatibilityResult);
            Assert.NotNull(verificationFailure);
            Assert.IsType<SqlException>(verificationFailure);
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
    }

    [Fact]
    public async Task OperationCanceledExceptionAfterRealMigrationPropagatesUnchanged()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        var activationCount = 0;
        var migrationCompleted = false;
        OperationCanceledException? emittedCancellation = null;

        var gate = new SqlServerPersistenceStartupGate(
            database.ConnectionString,
            new SqlPersistenceStartupOptions(LockTimeout),
            async (connectionString, lockTimeout, cancellationToken) =>
            {
                await RunRealMigrationAsync(connectionString, lockTimeout, cancellationToken);
                migrationCompleted = true;
            },
            (_, _, cancellationToken) =>
            {
                Assert.True(migrationCompleted);
                emittedCancellation = new OperationCanceledException(
                    "E.5.3 verification-stage cancellation.",
                    innerException: null,
                    cancellationToken);
                return Task.FromException<SqlRuntimeCompatibilityResult>(emittedCancellation);
            });

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () =>
            {
                await gate.EnsureReadyAsync(CancellationToken.None);
                activationCount++;
            });

        Assert.NotNull(emittedCancellation);
        Assert.Same(emittedCancellation, exception);
        Assert.Equal(0, activationCount);
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
            $"Failed to acquire E.5 verification blocker lock. Result: {result}.");
    }

    private static async Task AssertWaitingApplicationLockAsync(
        string connectionString,
        int sessionId,
        string expectedMode,
        Task startupTask,
        TimeSpan timeout)
    {
        await using var observer = new SqlConnection(connectionString);
        await observer.OpenAsync();
        using var timeoutSource = new CancellationTokenSource(timeout);

        try
        {
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
                        Assert.Equal(expectedMode, mode);
                        Assert.Equal("TRANSACTION", ownerType);
                        Assert.False(startupTask.IsCompleted);
                        return;
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(25), timeoutSource.Token);
            }
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            // Convert observer cancellation into one deterministic timeout assertion below.
        }

        throw new TimeoutException(
            $"SQL session {sessionId} was not observed waiting on the migration APPLICATION lock.");
    }

    private static async Task ExecuteNonQueryAsync(
        string connectionString,
        string sql,
        CancellationToken cancellationToken)
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
            "E.5.3 blocker cleanup failed.",
            cleanupFailures);

        if (primaryFailure is not null)
        {
            primaryFailure.Data["E.5.3 CleanupFailure"] = cleanupFailure;
            return;
        }

        throw cleanupFailure;
    }
}
