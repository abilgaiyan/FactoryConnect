using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerMigrationConcurrencyConformanceIntegrationTests
{
    private static readonly string[] MigrationManagedTables =
    [
        "dbo.ObservationStreamCheckpoint",
        "dbo.MachineObservation",
        "dbo.MetricInputStream",
        "dbo.MetricInputFact",
        "dbo.MetricAggregationProcessor",
        "dbo.MetricAggregationCheckpoint",
        "dbo.MetricAggregationContribution",
        "dbo.ShiftMetricAggregate",
        "dbo.ProductionDayMetricAggregate",
        "dbo.ProductionContextProcessor",
        "dbo.ProductionContextCheckpoint",
        "dbo.ContextualizedActivityOutput",
        "dbo.ProductionTimeEligibilityOutput",
    ];

    [Fact]
    public async Task ConcurrentFreshInstallInvocationsSerializeAndConvergeToOneCanonicalHistory()
    {
        await using var database = await IsolatedMigrationDatabase.CreateAsync();
        await using var firstConnection = database.CreateConnection();
        await using var secondConnection = database.CreateConnection();
        await firstConnection.OpenAsync();
        await secondConnection.OpenAsync();

        var catalog = SqlMigrationCatalog.Load();
        var firstClock = new FixedUtcClock(new DateTimeOffset(2026, 9, 4, 1, 0, 0, TimeSpan.Zero));
        var secondClock = new FixedUtcClock(new DateTimeOffset(2026, 9, 4, 2, 0, 0, TimeSpan.Zero));
        var firstEngine = new SqlServerMigrationEngine(catalog, firstClock);
        var secondEngine = new SqlServerMigrationEngine(catalog, secondClock);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstApply = ApplyAfterSignalAsync(firstEngine, firstConnection, start.Task);
        var secondApply = ApplyAfterSignalAsync(secondEngine, secondConnection, start.Task);
        start.SetResult();

        await Task.WhenAll(firstApply, secondApply);

        var history = await ReadHistoryAsync(firstConnection);
        Assert.Equal(4, history.Length);
        Assert.Equal(new[] { 1, 2, 3, 4 }, history.Select(static row => row.MigrationId).ToArray());
        var winningTimestamp = history[0].AppliedAtUtc;
        Assert.True(
            winningTimestamp == firstClock.UtcNow || winningTimestamp == secondClock.UtcNow,
            $"Unexpected migration history timestamp '{winningTimestamp:O}'.");
        Assert.All(history, row => Assert.Equal(winningTimestamp, row.AppliedAtUtc));
        await AssertCurrentStateAsync(firstConnection, catalog);
    }

    [Fact]
    public async Task ConcurrentFreshInstallMigration003FailuresRollbackIndependentlyAndConcurrentRetryConverges()
    {
        await using var database = await IsolatedMigrationDatabase.CreateAsync();
        await using var setupConnection = database.CreateConnection();
        await setupConnection.OpenAsync();
        await ExecuteAsync(
            setupConnection,
            """
            CREATE TABLE dbo.C5ConstraintConflict
            (
                Id int NOT NULL,
                CONSTRAINT UQ_MetricInputStream_RowMachine UNIQUE (Id)
            );
            """);

        await using var firstConnection = database.CreateConnection();
        await using var secondConnection = database.CreateConnection();
        await firstConnection.OpenAsync();
        await secondConnection.OpenAsync();

        var catalog = SqlMigrationCatalog.Load();
        var firstClock = new FixedUtcClock(new DateTimeOffset(2026, 9, 4, 3, 0, 0, TimeSpan.Zero));
        var secondClock = new FixedUtcClock(new DateTimeOffset(2026, 9, 4, 4, 0, 0, TimeSpan.Zero));
        var firstEngine = new SqlServerMigrationEngine(catalog, firstClock);
        var secondEngine = new SqlServerMigrationEngine(catalog, secondClock);
        var failureStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstFailure = CaptureMigration003FailureAfterSignalAsync(
            firstEngine,
            firstConnection,
            failureStart.Task);
        var secondFailure = CaptureMigration003FailureAfterSignalAsync(
            secondEngine,
            secondConnection,
            failureStart.Task);
        failureStart.SetResult();

        var failures = await Task.WhenAll(firstFailure, secondFailure);
        Assert.All(failures, AssertMigration003Failure);
        Assert.False(await ObjectExistsAsync(setupConnection, "dbo.FactoryConnectMigrationHistory"));
        foreach (var tableName in MigrationManagedTables)
        {
            Assert.False(await ObjectExistsAsync(setupConnection, tableName));
        }

        Assert.True(await ObjectExistsAsync(setupConnection, "dbo.C5ConstraintConflict"));
        Assert.True(await ConstraintExistsOnTableAsync(
            setupConnection,
            "UQ_MetricInputStream_RowMachine",
            "C5ConstraintConflict"));

        await ExecuteAsync(setupConnection, "DROP TABLE dbo.C5ConstraintConflict;");

        var retryStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRetry = ApplyAfterSignalAsync(firstEngine, firstConnection, retryStart.Task);
        var secondRetry = ApplyAfterSignalAsync(secondEngine, secondConnection, retryStart.Task);
        retryStart.SetResult();

        await Task.WhenAll(firstRetry, secondRetry);

        var history = await ReadHistoryAsync(setupConnection);
        Assert.Equal(4, history.Length);
        Assert.Equal(new[] { 1, 2, 3, 4 }, history.Select(static row => row.MigrationId).ToArray());
        var winningTimestamp = history[0].AppliedAtUtc;
        Assert.True(
            winningTimestamp == firstClock.UtcNow || winningTimestamp == secondClock.UtcNow,
            $"Unexpected migration history timestamp '{winningTimestamp:O}'.");
        Assert.All(history, row => Assert.Equal(winningTimestamp, row.AppliedAtUtc));
        await AssertCurrentStateAsync(setupConnection, catalog);
    }

    private static async Task ApplyAfterSignalAsync(
        SqlServerMigrationEngine engine,
        SqlConnection connection,
        Task startSignal)
    {
        await startSignal;
        await engine.ApplyAsync(connection, TimeSpan.FromSeconds(30), CancellationToken.None);
    }

    private static async Task<MigrationExecutionException> CaptureMigration003FailureAfterSignalAsync(
        SqlServerMigrationEngine engine,
        SqlConnection connection,
        Task startSignal)
    {
        await startSignal;
        return await Assert.ThrowsAsync<MigrationExecutionException>(() =>
            engine.ApplyAsync(connection, TimeSpan.FromSeconds(30), CancellationToken.None));
    }

    private static void AssertMigration003Failure(MigrationExecutionException exception)
    {
        Assert.Equal(3, exception.MigrationId);
        Assert.Equal("BindMetricInputFactMachine", exception.MigrationName);
        Assert.IsType<SqlException>(exception.InnerException);
    }

    private static async Task<HistoryRow[]> ReadHistoryAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MigrationId, AppliedAtUtc
            FROM dbo.FactoryConnectMigrationHistory
            ORDER BY MigrationId;
            """;
        var rows = new List<HistoryRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new HistoryRow(reader.GetInt32(0), reader.GetDateTimeOffset(1)));
        }

        return rows.ToArray();
    }

    private static async Task AssertCurrentStateAsync(
        SqlConnection connection,
        SqlMigrationCatalog catalog)
    {
        await using var transaction = connection.BeginTransaction();
        var ledgerReader = new SqlServerMigrationLedgerMetadataReader();
        var ledgerState = await ledgerReader.ResolveObjectAsync(
            connection,
            transaction,
            CancellationToken.None);
        Assert.Equal(SqlMigrationLedgerObjectKind.UserTable, ledgerState.Kind);
        var ledgerSchema = await ledgerReader.ReadSchemaAsync(
            connection,
            transaction,
            Assert.IsType<int>(ledgerState.ObjectId),
            CancellationToken.None);
        SqlMigrationLedgerSchemaValidator.Validate(ledgerSchema);

        var historyStore = new SqlServerMigrationHistoryStore(new FixedUtcClock(DateTimeOffset.UnixEpoch));
        var history = await historyStore.ReadAsync(connection, transaction, CancellationToken.None);
        Assert.Equal(
            catalog.Migrations.Length,
            SqlMigrationHistoryPrefixValidator.ValidateExactPrefix(history, catalog));

        var schemaReader = new SqlServerSchemaMetadataReader();
        var schema = await schemaReader.ReadFactoryConnectOwnedSchemaInTransactionAsync(
            connection,
            transaction,
            CancellationToken.None);
        var comparison = SqlSchemaComparator.Compare(SqlRepositorySchemaDescriptors.Current, schema);
        Assert.True(comparison.IsExactMatch, string.Join(Environment.NewLine, comparison.Differences));
        await transaction.RollbackAsync();
    }

    private static async Task<bool> ObjectExistsAsync(SqlConnection connection, string objectName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT OBJECT_ID(@ObjectName, N'U');";
        command.Parameters.AddWithValue("@ObjectName", objectName);
        var result = await command.ExecuteScalarAsync();
        return result is not null && result != DBNull.Value;
    }

    private static async Task<bool> ConstraintExistsOnTableAsync(
        SqlConnection connection,
        string constraintName,
        string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM sys.objects AS o
            INNER JOIN sys.tables AS t ON t.object_id = o.parent_object_id
            WHERE o.name = @ConstraintName
              AND t.name = @TableName;
            """;
        command.Parameters.AddWithValue("@ConstraintName", constraintName);
        command.Parameters.AddWithValue("@TableName", tableName);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed record HistoryRow(int MigrationId, DateTimeOffset AppliedAtUtc);

    private sealed class FixedUtcClock : ISqlMigrationUtcClock
    {
        public FixedUtcClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; }
    }

    private sealed class IsolatedMigrationDatabase : IAsyncDisposable
    {
        private readonly string _adminConnectionString;
        private readonly string _databaseName;
        private bool _exists;

        private IsolatedMigrationDatabase(
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

        public static async Task<IsolatedMigrationDatabase> CreateAsync()
        {
            var sourceConnectionString = Environment.GetEnvironmentVariable(
                SqlServerTestDatabaseFixture.ConnectionStringEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(sourceConnectionString))
            {
                throw new InvalidOperationException(
                    $"{SqlServerTestDatabaseFixture.ConnectionStringEnvironmentVariable} is required for SQL Server integration tests.");
            }

            var sourceBuilder = new SqlConnectionStringBuilder(sourceConnectionString);
            var databaseName = $"FactoryConnect_FC030_C5_{Guid.NewGuid():N}";
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

            return new IsolatedMigrationDatabase(
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
