using System.Globalization;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlPersistenceStartupCommitBoundaryRealSqlIntegrationTests
{
    private static readonly TimeSpan StartupLockTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task StartupWaitsForExternalCommitThenRealMigrationOwnsCommittedSchemaDriftRejection()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await RunRealMigrationAsync(database.ConnectionString);

        SqlConnection? blockerConnection = null;
        SqlTransaction? blockerTransaction = null;
        Exception? migrationFailure = null;
        Exception? primaryFailure = null;
        var verificationCount = 0;
        var activationCount = 0;
        var migrationSessionId = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            (blockerConnection, blockerTransaction) =
                await BeginCommittedSchemaDriftTransactionAsync(database.ConnectionString);

            var gate = new SqlServerPersistenceStartupGate(
                database.ConnectionString,
                new SqlPersistenceStartupOptions(StartupLockTimeout),
                async (connectionString, lockTimeout, cancellationToken) =>
                {
                    await using var connection = new SqlConnection(connectionString);
                    await connection.OpenAsync(cancellationToken);
                    migrationSessionId.TrySetResult(
                        await ReadSessionIdAsync(connection, cancellationToken));
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
                async (connectionString, lockTimeout, cancellationToken) =>
                {
                    verificationCount++;
                    await using var connection = new SqlConnection(connectionString);
                    await connection.OpenAsync(cancellationToken);
                    return await SqlServerRuntimeSchemaCompatibilityVerifier.CreateDefault().VerifyAsync(
                        connection,
                        lockTimeout,
                        cancellationToken);
                });

            async Task RunStartupAsync()
            {
                await gate.EnsureReadyAsync(CancellationToken.None);
                activationCount++;
            }

            var startupTask = RunStartupAsync();
            var sessionId = await migrationSessionId.Task.WaitAsync(ObservationTimeout);

            await AssertWaitingExclusiveApplicationLockAsync(
                database.ConnectionString,
                sessionId,
                startupTask,
                ObservationTimeout);
            Assert.Equal(0, verificationCount);
            Assert.Equal(0, activationCount);

            await blockerTransaction.CommitAsync();
            await blockerTransaction.DisposeAsync();
            blockerTransaction = null;
            await blockerConnection.DisposeAsync();
            blockerConnection = null;

            var exception = await Assert.ThrowsAsync<SqlPersistenceStartupException>(
                () => startupTask.WaitAsync(CompletionTimeout));
            primaryFailure = exception;

            Assert.Equal(
                SqlPersistenceStartupFailureKind.MigrationOperationalFailure,
                exception.FailureKind);
            Assert.Null(exception.CompatibilityResult);
            Assert.NotNull(migrationFailure);
            Assert.IsType<FinalSchemaValidationException>(migrationFailure);
            Assert.Same(migrationFailure, exception.InnerException);
            Assert.Equal(0, verificationCount);
            Assert.Equal(0, activationCount);

            var committedResult = await RunRealVerificationAsync(database.ConnectionString);
            Assert.Equal(
                SqlRuntimeCompatibilityClassification.MigrationSchemaDrift,
                committedResult.Classification);
            Assert.False(committedResult.IsCompatible);
            Assert.NotEmpty(committedResult.Diagnostics);
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

    private static async Task RunRealMigrationAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await SqlServerMigrationEngine.CreateDefault().ApplyAsync(
            connection,
            StartupLockTimeout,
            CancellationToken.None);
    }

    private static async Task<SqlRuntimeCompatibilityResult> RunRealVerificationAsync(
        string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        return await SqlServerRuntimeSchemaCompatibilityVerifier.CreateDefault().VerifyAsync(
            connection,
            StartupLockTimeout,
            CancellationToken.None);
    }

    private static async Task<(SqlConnection Connection, SqlTransaction Transaction)>
        BeginCommittedSchemaDriftTransactionAsync(string connectionString)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync();
            var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
            try
            {
                await AcquireExclusiveMigrationLockAsync(connection, transaction);

                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    "DROP INDEX IX_MetricInputFact_OrderedRead ON dbo.MetricInputFact;";
                await command.ExecuteNonQueryAsync();
                return (connection, transaction);
            }
            catch
            {
                await transaction.DisposeAsync();
                throw;
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
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
            $"Failed to acquire E.5.5 external migration lock. Result: {result}.");
    }

    private static async Task AssertWaitingExclusiveApplicationLockAsync(
        string connectionString,
        int sessionId,
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

                await using var reader = await command.ExecuteReaderAsync(timeoutSource.Token);
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
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            // Normalize observer cancellation to one deterministic timeout below.
        }

        throw new TimeoutException(
            $"SQL session {sessionId} was not observed waiting on the E.5.5 migration APPLICATION lock.");
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
            "E.5.5 commit-boundary blocker cleanup failed.",
            cleanupFailures);
        if (primaryFailure is not null)
        {
            primaryFailure.Data["E.5.5 CleanupFailure"] = cleanupFailure;
            return;
        }

        throw cleanupFailure;
    }
}
