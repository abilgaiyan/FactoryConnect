using System.Globalization;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlPersistenceStartupConcurrentRealSqlIntegrationTests
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData("API", "Edge")]
    [InlineData("Edge", "API")]
    public async Task ConcurrentLabelledGatesSerializeThroughRealMigrationLockAndConverge(
        string winningLabel,
        string waitingLabel)
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await SeedPrefixAsync(database.ConnectionString, prefixLength: 2);

        SqlConnection? schemaBlockerConnection = null;
        SqlTransaction? schemaBlockerTransaction = null;
        Exception? primaryFailure = null;

        try
        {
            (schemaBlockerConnection, schemaBlockerTransaction) =
                await AcquireMigration003SchemaBlockerAsync(database.ConnectionString);

            var winner = CreateLabelledGate(database.ConnectionString, winningLabel);
            var waiter = CreateLabelledGate(database.ConnectionString, waitingLabel);

            var winnerTask = RunAndActivateAsync(winner);
            var winnerSessionId = await winner.MigrationSessionId.Task.WaitAsync(ObservationTimeout);

            await AssertApplicationLockAsync(
                database.ConnectionString,
                winnerSessionId,
                expectedStatus: "GRANT",
                expectedMode: "X",
                winnerTask,
                expectIncomplete: true,
                ObservationTimeout);

            var waiterTask = RunAndActivateAsync(waiter);
            var waiterSessionId = await waiter.MigrationSessionId.Task.WaitAsync(ObservationTimeout);

            Assert.NotEqual(winnerSessionId, waiterSessionId);
            await AssertApplicationLockAsync(
                database.ConnectionString,
                waiterSessionId,
                expectedStatus: "WAIT",
                expectedMode: "X",
                waiterTask,
                expectIncomplete: true,
                ObservationTimeout);
            Assert.False(winnerTask.IsCompleted);

            await schemaBlockerTransaction.RollbackAsync();
            await schemaBlockerTransaction.DisposeAsync();
            schemaBlockerTransaction = null;
            await schemaBlockerConnection.DisposeAsync();
            schemaBlockerConnection = null;

            await Task.WhenAll(
                winnerTask.WaitAsync(CompletionTimeout),
                waiterTask.WaitAsync(CompletionTimeout));

            Assert.Equal(1, winner.ActivationCount);
            Assert.Equal(1, waiter.ActivationCount);
            Assert.Equal(1, winner.VerificationCount);
            Assert.Equal(1, waiter.VerificationCount);
            Assert.NotNull(winner.VerificationResult);
            Assert.NotNull(waiter.VerificationResult);
            Assert.Equal(
                SqlRuntimeCompatibilityClassification.Compatible,
                winner.VerificationResult.Classification);
            Assert.Equal(
                SqlRuntimeCompatibilityClassification.Compatible,
                waiter.VerificationResult.Classification);
            Assert.True(winner.VerificationResult.IsCompatible);
            Assert.True(waiter.VerificationResult.IsCompatible);
            Assert.Empty(winner.VerificationResult.Diagnostics);
            Assert.Empty(waiter.VerificationResult.Diagnostics);

            await AssertExactCurrentStateAsync(database.ConnectionString);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            await CleanupBlockerAsync(
                schemaBlockerConnection,
                schemaBlockerTransaction,
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
        AcquireMigration003SchemaBlockerAsync(string connectionString)
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
                    SELECT TOP (0) MetricInputFactRowId
                    FROM dbo.MetricInputFact WITH (TABLOCKX, HOLDLOCK);
                    """;
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

    private static async Task AssertApplicationLockAsync(
        string connectionString,
        int sessionId,
        string expectedStatus,
        string expectedMode,
        Task startupTask,
        bool expectIncomplete,
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
                    if (string.Equals(status, expectedStatus, StringComparison.Ordinal))
                    {
                        Assert.Equal(expectedMode, mode);
                        Assert.Equal("TRANSACTION", ownerType);
                        if (expectIncomplete)
                        {
                            Assert.False(startupTask.IsCompleted);
                        }
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
            $"SQL session {sessionId} was not observed with {expectedStatus}/{expectedMode} on the migration APPLICATION lock.");
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
            Assert.Equal(
                catalog.Migrations.Select(static migration => migration.MigrationId),
                history.Select(static row => row.MigrationId));
            Assert.Equal(
                catalog.Migrations.Select(static migration => migration.Name),
                history.Select(static row => row.Name));
            Assert.Equal(
                catalog.Migrations.Select(static migration => migration.Sha256Checksum),
                history.Select(static row => row.CanonicalChecksum));

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
            "E.5.4 schema blocker cleanup failed.",
            cleanupFailures);
        if (primaryFailure is not null)
        {
            primaryFailure.Data["E.5.4 CleanupFailure"] = cleanupFailure;
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
