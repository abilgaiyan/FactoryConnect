using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlPersistenceStartupRealSqlMigrationFailureIntegrationTests
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(MigrationFailureState.LedgerOccupiedByView)]
    [InlineData(MigrationFailureState.InvalidLedgerStructure)]
    [InlineData(MigrationFailureState.MalformedHistory)]
    [InlineData(MigrationFailureState.UnledgeredPartialSchema)]
    [InlineData(MigrationFailureState.ChecksumMismatch)]
    public async Task RealMigrationRejectionIsPreservedAtStartupBoundary(
        MigrationFailureState initialState)
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await SeedFailureStateAsync(database.ConnectionString, initialState);

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
        Assert.Equal(0, verificationCount);
        Assert.Equal(0, activationCount);
    }

    [Fact]
    public async Task MigrationLockTimeoutIsPreservedAtStartupBoundary()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var blocker = new SqlConnection(database.ConnectionString);
        await blocker.OpenAsync();
        await using var blockerTransaction =
            (SqlTransaction)await blocker.BeginTransactionAsync();
        await AcquireExclusiveMigrationLockAsync(blocker, blockerTransaction);

        Exception? migrationFailure = null;
        var verificationCount = 0;
        var activationCount = 0;
        var gate = CreateGate(
            database.ConnectionString,
            exception => migrationFailure = exception,
            () => verificationCount++,
            TimeSpan.Zero);

        try
        {
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
            Assert.Equal(0, verificationCount);
            Assert.Equal(0, activationCount);
        }
        finally
        {
            await blockerTransaction.RollbackAsync();
        }
    }

    [Fact]
    public async Task Migration003FailureRetainsPrefixAndRetrySucceeds()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await SeedPrefixAsync(database.ConnectionString, prefixLength: 2);
        await ExecuteNonQueryAsync(
            database.ConnectionString,
            """
            CREATE TABLE dbo.E5Migration003Conflict
            (
                Id int NOT NULL,
                CONSTRAINT UQ_MetricInputStream_RowMachine UNIQUE (Id)
            );
            """);

        Exception? migrationFailure = null;
        var verificationCount = 0;
        var activationCount = 0;
        var firstGate = CreateGate(
            database.ConnectionString,
            exception => migrationFailure = exception,
            () => verificationCount++);

        var firstException = await Assert.ThrowsAsync<SqlPersistenceStartupException>(
            async () =>
            {
                await firstGate.EnsureReadyAsync(CancellationToken.None);
                activationCount++;
            });

        Assert.Equal(
            SqlPersistenceStartupFailureKind.MigrationOperationalFailure,
            firstException.FailureKind);
        Assert.Null(firstException.CompatibilityResult);
        Assert.NotNull(migrationFailure);
        Assert.Same(migrationFailure, firstException.InnerException);
        var migrationException = Assert.IsType<MigrationExecutionException>(
            firstException.InnerException);
        Assert.Equal(3, migrationException.MigrationId);
        Assert.Equal("BindMetricInputFactMachine", migrationException.MigrationName);
        Assert.IsType<SqlException>(migrationException.InnerException);
        Assert.Equal(0, verificationCount);
        Assert.Equal(0, activationCount);
        await AssertExactHistoryPrefixAsync(database.ConnectionString, prefixLength: 2);

        await ExecuteNonQueryAsync(
            database.ConnectionString,
            "DROP TABLE dbo.E5Migration003Conflict;");

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
        Action onVerification,
        TimeSpan? lockTimeout = null) =>
        new(
            connectionString,
            new SqlPersistenceStartupOptions(lockTimeout ?? LockTimeout),
            async (stageConnectionString, stageLockTimeout, cancellationToken) =>
            {
                try
                {
                    await RunRealMigrationAsync(
                        stageConnectionString,
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
                    new InvalidOperationException("Verification must not execute after migration failure."));
            });

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

    private static async Task SeedFailureStateAsync(
        string connectionString,
        MigrationFailureState initialState)
    {
        switch (initialState)
        {
            case MigrationFailureState.LedgerOccupiedByView:
                await ExecuteNonQueryAsync(
                    connectionString,
                    "CREATE VIEW dbo.FactoryConnectMigrationHistory AS SELECT CAST(1 AS int) AS MigrationId;");
                return;

            case MigrationFailureState.InvalidLedgerStructure:
                await ExecuteNonQueryAsync(
                    connectionString,
                    "CREATE TABLE dbo.FactoryConnectMigrationHistory (MigrationId int NOT NULL);");
                return;

            case MigrationFailureState.MalformedHistory:
                await SeedLedgerRowAsync(
                    connectionString,
                    migrationId: 2,
                    name: "DurableMetricAggregation",
                    checksum: new string('0', 64));
                return;

            case MigrationFailureState.UnledgeredPartialSchema:
                await SeedUnledgeredMigration001Async(connectionString);
                return;

            case MigrationFailureState.ChecksumMismatch:
                var migration001 = SqlMigrationCatalog.Load().Migrations[0];
                await SeedLedgerRowAsync(
                    connectionString,
                    migration001.MigrationId,
                    migration001.Name,
                    new string('0', 64));
                return;

            default:
                throw new InvalidOperationException(
                    $"Unsupported E.5 migration failure state '{initialState}'.");
        }
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

    private static async Task SeedUnledgeredMigration001Async(string connectionString)
    {
        var migration001 = SqlMigrationCatalog.Load().Migrations[0];
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction =
            (SqlTransaction)await connection.BeginTransactionAsync();
        await SqlServerMigrationExecutor.ExecuteAsync(
            connection,
            transaction,
            migration001,
            CancellationToken.None);
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
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(result >= 0, $"Failed to acquire E.5 migration blocker lock. Result: {result}.");
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

    public enum MigrationFailureState
    {
        LedgerOccupiedByView,
        InvalidLedgerStructure,
        MalformedHistory,
        UnledgeredPartialSchema,
        ChecksumMismatch,
    }
}
