using System.Globalization;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlPersistenceStartupConcurrentEmptyDatabaseIntegrationTests
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task TwoLabelledGatesFromEmptyDatabaseConvergeToOneExactInstallation()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();

        SqlConnection? barrierConnection = null;
        SqlTransaction? barrierTransaction = null;
        Exception? primaryFailure = null;

        try
        {
            (barrierConnection, barrierTransaction) =
                await AcquireExternalMigrationBarrierAsync(database.ConnectionString);

            var api = CreateLabelledGate(database.ConnectionString, "API");
            var edge = CreateLabelledGate(database.ConnectionString, "Edge");

            var apiTask = RunAndActivateAsync(api);
            var edgeTask = RunAndActivateAsync(edge);

            var apiSessionId = await api.MigrationSessionId.Task.WaitAsync(ObservationTimeout);
            var edgeSessionId = await edge.MigrationSessionId.Task.WaitAsync(ObservationTimeout);
            Assert.NotEqual(apiSessionId, edgeSessionId);

            await AssertWaitingExclusiveApplicationLockAsync(
                database.ConnectionString,
                apiSessionId,
                apiTask,
                ObservationTimeout);
            await AssertWaitingExclusiveApplicationLockAsync(
                database.ConnectionString,
                edgeSessionId,
                edgeTask,
                ObservationTimeout);

            Assert.False(apiTask.IsCompleted);
            Assert.False(edgeTask.IsCompleted);
            await AssertDatabaseStillUninitializedAsync(database.ConnectionString);

            await barrierTransaction.RollbackAsync();
            await barrierTransaction.DisposeAsync();
            barrierTransaction = null;
            await barrierConnection.DisposeAsync();
            barrierConnection = null;

            await Task.WhenAll(
                apiTask.WaitAsync(CompletionTimeout),
                edgeTask.WaitAsync(CompletionTimeout));

            Assert.Equal(1, api.ActivationCount);
            Assert.Equal(1, edge.ActivationCount);
            Assert.Equal(1, api.VerificationCount);
            Assert.Equal(1, edge.VerificationCount);
            AssertCompatible(api);
            AssertCompatible(edge);

            await AssertExactCurrentStateAsync(database.ConnectionString);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            await CleanupBarrierAsync(
                barrierConnection,
                barrierTransaction,
                primaryFailure);
        }
    }

    private static LabelledStartupGate CreateLabelledGate(
        string connectionString,
        string label)
    {
        var state = new LabelledStartupGate(label);
        state.Gate = new SqlServerPersistenceStartupGate(
            connectionString,
            new SqlPersistenceStartupOptions(LockTimeout),
            async (stageConnectionString, lockTimeout, cancellationToken) =>
            {
                await using var connection = new SqlConnection(stageConnectionString);
                await connection.OpenAsync(cancellationToken);
                state.MigrationSessionId.TrySetResult(
                    await ReadSessionIdAsync(connection, cancellationToken));
                await SqlServerMigrationEngine.CreateDefault().ApplyAsync(
                    connection,
                    lockTimeout,
                    cancellationToken);
            },
            async (stageConnectionString, lockTimeout, cancellationToken) =>
            {
                state.VerificationCount++;
                await using var connection = new SqlConnection(stageConnectionString);
                await connection.OpenAsync(cancellationToken);
                state.VerificationResult =
                    await SqlServerRuntimeSchemaCompatibilityVerifier.CreateDefault().VerifyAsync(
                        connection,
                        lockTimeout,
                        cancellationToken);
                return state.VerificationResult;
            });
        return state;
    }

    private static async Task RunAndActivateAsync(LabelledStartupGate state)
    {
        await state.Gate.EnsureReadyAsync(CancellationToken.None);
        state.ActivationCount++;
    }

    private static async Task<(SqlConnection Connection, SqlTransaction Transaction)>
        AcquireExternalMigrationBarrierAsync(string connectionString)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync();
            var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
            try
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
                Assert.True(result >= 0, $"Failed to acquire E.5.4 empty-database barrier. Result: {result}.");
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
            // Normalize observation timeout below.
        }

        throw new TimeoutException(
            $"SQL session {sessionId} was not observed waiting for the E.5.4 migration APPLICATION lock.");
    }

    private static async Task AssertDatabaseStillUninitializedAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                CASE WHEN OBJECT_ID(N'dbo.FactoryConnectMigrationHistory', N'U') IS NULL THEN 0 ELSE 1 END,
                COUNT(*)
            FROM sys.tables
            WHERE schema_id = SCHEMA_ID(N'dbo')
              AND name IN
              (
                  N'RawObservation',
                  N'MetricInputStream',
                  N'MetricInputFact',
                  N'ProductionContextProcessor'
              );
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0, reader.GetInt32(0));
        Assert.Equal(0, reader.GetInt32(1));
    }

    private static void AssertCompatible(LabelledStartupGate state)
    {
        Assert.NotNull(state.VerificationResult);
        Assert.Equal(
            SqlRuntimeCompatibilityClassification.Compatible,
            state.VerificationResult.Classification);
        Assert.True(state.VerificationResult.IsCompatible);
        Assert.Empty(state.VerificationResult.Diagnostics);
    }

    private static async Task AssertExactCurrentStateAsync(string connectionString)
    {
        var catalog = SqlMigrationCatalog.Load();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        var verifier = SqlServerRuntimeSchemaCompatibilityVerifier.CreateDefault();
        var result = await verifier.VerifyAsync(
            connection,
            LockTimeout,
            CancellationToken.None);
        Assert.Equal(SqlRuntimeCompatibilityClassification.Compatible, result.Classification);
        Assert.True(result.IsCompatible);
        Assert.Empty(result.Diagnostics);

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            var history = await new SqlServerMigrationHistoryStore(
                    new SystemSqlMigrationUtcClock())
                .ReadAsync(connection, transaction, CancellationToken.None);
            Assert.Equal(catalog.Migrations.Length, history.Length);
            for (var index = 0; index < catalog.Migrations.Length; index++)
            {
                Assert.Equal(catalog.Migrations[index].MigrationId, history[index].MigrationId);
                Assert.Equal(catalog.Migrations[index].Name, history[index].Name);
                Assert.Equal(catalog.Migrations[index].Sha256Checksum, history[index].CanonicalChecksum);
            }

            var liveSchema = await new SqlServerSchemaMetadataReader()
                .ReadFactoryConnectOwnedSchemaInTransactionAsync(
                    connection,
                    transaction,
                    CancellationToken.None);
            var comparison = SqlSchemaComparator.Compare(
                SqlRepositorySchemaDescriptors.Current,
                liveSchema);
            Assert.True(
                comparison.IsExactMatch,
                string.Join(Environment.NewLine, comparison.Differences));
        }
        finally
        {
            await transaction.RollbackAsync();
        }
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

    private static async Task CleanupBarrierAsync(
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
            "E.5.4 empty-database barrier cleanup failed.",
            cleanupFailures);
        if (primaryFailure is not null)
        {
            primaryFailure.Data["E.5.4 Empty CleanupFailure"] = cleanupFailure;
            return;
        }

        throw cleanupFailure;
    }

    private sealed class LabelledStartupGate
    {
        public LabelledStartupGate(string label)
        {
            Label = label;
        }

        public string Label { get; }

        public SqlServerPersistenceStartupGate Gate { get; set; } = null!;

        public TaskCompletionSource<int> MigrationSessionId { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int ActivationCount { get; set; }

        public int VerificationCount { get; set; }

        public SqlRuntimeCompatibilityResult? VerificationResult { get; set; }
    }
}
