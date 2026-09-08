using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Collection(SqlStartupConcurrencyDefinition.CollectionName)]
[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerMigration006ConcurrencyIntegrationTests
{
    private static readonly int[] MigrationIdsThrough006 = [1, 2, 3, 4, 5, 6];
    private static readonly TimeSpan MigrationLockTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task TwoCallersFromExactPost005SerializeAndBothObserveExactPost006()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var setupConnection = new SqlConnection(database.ConnectionString);
        await using var blockerConnection = new SqlConnection(database.ConnectionString);
        await using var firstConnection = new SqlConnection(database.ConnectionString);
        await using var secondConnection = new SqlConnection(database.ConnectionString);
        await using var observerConnection = new SqlConnection(database.ConnectionString);
        await setupConnection.OpenAsync();
        await blockerConnection.OpenAsync();
        await firstConnection.OpenAsync();
        await secondConnection.OpenAsync();
        await observerConnection.OpenAsync();

        var catalog = SqlMigrationCatalog.Load();
        await CreateExactPrefixAsync(setupConnection, catalog, prefixLength: 5);

        var blockerSessionId = await ReadSessionIdAsync(blockerConnection);
        var firstSessionId = await ReadSessionIdAsync(firstConnection);
        var secondSessionId = await ReadSessionIdAsync(secondConnection);
        var firstClock = new FixedUtcClock(new DateTimeOffset(2026, 9, 8, 5, 0, 0, TimeSpan.Zero));
        var secondClock = new FixedUtcClock(new DateTimeOffset(2026, 9, 8, 6, 0, 0, TimeSpan.Zero));
        var firstEngine = new SqlServerMigrationEngine(catalog, firstClock);
        var secondEngine = new SqlServerMigrationEngine(catalog, secondClock);

        await using var blockerTransaction = blockerConnection.BeginTransaction();
        await HoldManifestTableLockAsync(blockerConnection, blockerTransaction);

        var firstApply = RunCallerAsync(firstEngine, firstConnection, firstSessionId);
        await WaitForFirstCallerToOwnMigrationLockAndBlockOnManifestAsync(
            observerConnection,
            firstSessionId,
            blockerSessionId,
            firstApply);

        var secondApply = RunCallerAsync(secondEngine, secondConnection, secondSessionId);
        await WaitForSecondCallerToWaitOnFirstMigrationLockAsync(
            observerConnection,
            secondSessionId,
            firstSessionId,
            secondApply);

        Assert.False(firstApply.IsCompleted);
        Assert.False(secondApply.IsCompleted);

        await blockerTransaction.RollbackAsync();

        var outcomes = await Task.WhenAll(firstApply, secondApply);
        Assert.All(outcomes, static outcome => Assert.True(outcome.CompletedSuccessfully));
        Assert.Equal(firstSessionId, outcomes[0].SessionId);
        Assert.Equal(secondSessionId, outcomes[1].SessionId);
        Assert.Equal(MigrationIdsThrough006, outcomes[0].ObservedMigrationIds);
        Assert.Equal(MigrationIdsThrough006, outcomes[1].ObservedMigrationIds);

        var migration006Rows = await ReadMigration006RowsAsync(setupConnection);
        var migration006 = Assert.Single(migration006Rows);
        Assert.Equal("CorrectOperationalMetricProjectionManifestParent", migration006.Name);
        Assert.Equal(
            "DDFD8C6CC1FADE9D7486EB5D3D915881A2538E2724A6B57EDA7E6D7C16EB6EEE",
            migration006.CanonicalChecksum);
        Assert.Equal(firstClock.UtcNow, migration006.AppliedAtUtc);
        Assert.NotEqual(secondClock.UtcNow, migration006.AppliedAtUtc);

        await AssertPost006ForeignKeyStateAsync(setupConnection);
        await AssertCurrentStateAsync(setupConnection, catalog);
    }

    private static async Task<MigrationCallerOutcome> RunCallerAsync(
        SqlServerMigrationEngine engine,
        SqlConnection connection,
        int sessionId)
    {
        await engine.ApplyAsync(connection, MigrationLockTimeout, CancellationToken.None);
        return new MigrationCallerOutcome(
            sessionId,
            CompletedSuccessfully: true,
            await ReadMigrationIdsAsync(connection));
    }

    private static async Task CreateExactPrefixAsync(
        SqlConnection connection,
        SqlMigrationCatalog catalog,
        int prefixLength)
    {
        Assert.InRange(prefixLength, 0, catalog.Migrations.Length);
        await using var transaction = connection.BeginTransaction();
        await SqlServerMigrationLedgerCreator.CreateAsync(connection, transaction, CancellationToken.None);
        var historyStore = new SqlServerMigrationHistoryStore(
            new FixedUtcClock(new DateTimeOffset(2026, 9, 8, 4, 0, 0, TimeSpan.Zero)));

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

    private static async Task HoldManifestTableLockAsync(
        SqlConnection connection,
        SqlTransaction transaction)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT_BIG(*)
            FROM dbo.OperationalMetricProjectionManifest WITH (TABLOCKX, HOLDLOCK);
            """;
        _ = await command.ExecuteScalarAsync();
    }

    private static async Task WaitForFirstCallerToOwnMigrationLockAndBlockOnManifestAsync(
        SqlConnection observerConnection,
        int firstSessionId,
        int blockerSessionId,
        Task firstApply)
    {
        var deadline = DateTime.UtcNow + ObservationTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var applicationLock = await ReadApplicationLockStatusAsync(observerConnection, firstSessionId);
            var request = await ReadRequestWaitAsync(observerConnection, firstSessionId);
            if (string.Equals(applicationLock, "GRANT", StringComparison.Ordinal)
                && request is not null
                && request.BlockingSessionId == blockerSessionId
                && request.WaitType.StartsWith("LCK_M_", StringComparison.Ordinal))
            {
                Assert.False(firstApply.IsCompleted);
                return;
            }

            if (firstApply.IsCompleted)
            {
                throw new Xunit.Sdk.XunitException(
                    "The first Migration 006 caller completed before its granted migration lock and blocked DDL were observed.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new Xunit.Sdk.XunitException(
            $"Did not observe session {firstSessionId} owning the migration application lock while blocked by session {blockerSessionId}.");
    }

    private static async Task WaitForSecondCallerToWaitOnFirstMigrationLockAsync(
        SqlConnection observerConnection,
        int secondSessionId,
        int firstSessionId,
        Task secondApply)
    {
        var deadline = DateTime.UtcNow + ObservationTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var applicationLock = await ReadApplicationLockStatusAsync(observerConnection, secondSessionId);
            var request = await ReadRequestWaitAsync(observerConnection, secondSessionId);
            if (string.Equals(applicationLock, "WAIT", StringComparison.Ordinal)
                && request is not null
                && request.BlockingSessionId == firstSessionId
                && request.WaitType.StartsWith("LCK_M_", StringComparison.Ordinal))
            {
                Assert.False(secondApply.IsCompleted);
                return;
            }

            if (secondApply.IsCompleted)
            {
                throw new Xunit.Sdk.XunitException(
                    "The second Migration 006 caller completed before it was observed waiting behind the first caller's migration lock.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new Xunit.Sdk.XunitException(
            $"Did not observe session {secondSessionId} waiting behind migration-lock owner session {firstSessionId}.");
    }

    private static async Task<string?> ReadApplicationLockStatusAsync(
        SqlConnection connection,
        int sessionId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (1) request_status
            FROM sys.dm_tran_locks
            WHERE request_session_id = @SessionId
              AND resource_type = N'APPLICATION'
              AND request_mode = N'X'
            ORDER BY request_status;
            """;
        command.Parameters.AddWithValue("@SessionId", sessionId);
        return Convert.ToString(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<RequestWaitState?> ReadRequestWaitAsync(
        SqlConnection connection,
        int sessionId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT wait_type, blocking_session_id
            FROM sys.dm_exec_requests
            WHERE session_id = @SessionId;
            """;
        command.Parameters.AddWithValue("@SessionId", sessionId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        var state = new RequestWaitState(
            reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
            reader.GetInt16(1));
        Assert.False(await reader.ReadAsync());
        return state;
    }

    private static async Task<int> ReadSessionIdAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT @@SPID;";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<int[]> ReadMigrationIdsAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT MigrationId FROM dbo.FactoryConnectMigrationHistory ORDER BY MigrationId;";
        var migrationIds = new List<int>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            migrationIds.Add(reader.GetInt32(0));
        }

        return migrationIds.ToArray();
    }

    private static async Task<Migration006HistoryRow[]> ReadMigration006RowsAsync(
        SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Name, CanonicalChecksum, AppliedAtUtc
            FROM dbo.FactoryConnectMigrationHistory
            WHERE MigrationId = 6;
            """;
        var rows = new List<Migration006HistoryRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new Migration006HistoryRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetDateTimeOffset(2)));
        }

        return rows.ToArray();
    }

    private static async Task AssertPost006ForeignKeyStateAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                parent_table.name,
                referenced_table.name,
                parent_column.name,
                referenced_column.name,
                fk.is_disabled,
                fk.is_not_trusted
            FROM sys.foreign_keys AS fk
            INNER JOIN sys.tables AS parent_table
                ON parent_table.object_id = fk.parent_object_id
            INNER JOIN sys.tables AS referenced_table
                ON referenced_table.object_id = fk.referenced_object_id
            INNER JOIN sys.foreign_key_columns AS fkc
                ON fkc.constraint_object_id = fk.object_id
            INNER JOIN sys.columns AS parent_column
                ON parent_column.object_id = fkc.parent_object_id
                AND parent_column.column_id = fkc.parent_column_id
            INNER JOIN sys.columns AS referenced_column
                ON referenced_column.object_id = fkc.referenced_object_id
                AND referenced_column.column_id = fkc.referenced_column_id
            WHERE fk.name = N'FK_OperationalMetricProjectionManifest_Processor';
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("OperationalMetricProjectionManifest", reader.GetString(0));
        Assert.Equal("OperationalMetricProjectionProcessor", reader.GetString(1));
        Assert.Equal("OperationalMetricProjectionProcessorRowId", reader.GetString(2));
        Assert.Equal("OperationalMetricProjectionProcessorRowId", reader.GetString(3));
        Assert.False(reader.GetBoolean(4));
        Assert.False(reader.GetBoolean(5));
        Assert.False(await reader.ReadAsync());
    }

    private static async Task AssertCurrentStateAsync(
        SqlConnection connection,
        SqlMigrationCatalog catalog)
    {
        await using var transaction = connection.BeginTransaction();
        var historyStore = new SqlServerMigrationHistoryStore(
            new FixedUtcClock(DateTimeOffset.UnixEpoch));
        var history = await historyStore.ReadAsync(connection, transaction, CancellationToken.None);
        Assert.Equal(
            catalog.Migrations.Length,
            SqlMigrationHistoryPrefixValidator.ValidateExactPrefix(history, catalog));

        var schema = await new SqlServerSchemaMetadataReader()
            .ReadFactoryConnectOwnedSchemaInTransactionAsync(
                connection,
                transaction,
                CancellationToken.None);
        var comparison = SqlSchemaComparator.Compare(SqlRepositorySchemaDescriptors.Current, schema);
        Assert.True(comparison.IsExactMatch, string.Join(Environment.NewLine, comparison.Differences));
        await transaction.RollbackAsync();
    }

    private sealed record MigrationCallerOutcome(
        int SessionId,
        bool CompletedSuccessfully,
        int[] ObservedMigrationIds);

    private sealed record Migration006HistoryRow(
        string Name,
        string CanonicalChecksum,
        DateTimeOffset AppliedAtUtc);

    private sealed record RequestWaitState(
        string WaitType,
        int BlockingSessionId);

    private sealed class FixedUtcClock : ISqlMigrationUtcClock
    {
        public FixedUtcClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; }
    }
}
