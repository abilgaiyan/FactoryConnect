using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerMigrationLockIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerMigrationLockIntegrationTests(SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task BeginCreatesSerializableTransactionAndExclusiveTransactionOwnedLock()
    {
        await using var connection = await OpenConnectionAsync();
        await using var scope = await SqlServerMigrationTransactionScope.BeginAsync(
            connection,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(4, await ReadIsolationLevelAsync(connection, scope.Transaction));
        Assert.Equal(
            "Exclusive",
            await ReadMigrationLockModeAsync(connection, scope.Transaction));
    }

    [Fact]
    public async Task ContendingZeroWaitMigratorReceivesLockFailure()
    {
        await using var firstConnection = await OpenConnectionAsync();
        await using var firstScope = await SqlServerMigrationTransactionScope.BeginAsync(
            firstConnection,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
        await using var secondConnection = await OpenConnectionAsync();

        var exception = await Assert.ThrowsAsync<SqlMigrationLockAcquisitionException>(
            () => SqlServerMigrationTransactionScope.BeginAsync(
                secondConnection,
                TimeSpan.Zero,
                CancellationToken.None));

        Assert.NotNull(exception.ReturnCode);
        Assert.True(exception.ReturnCode.Value < 0);
        Assert.Equal(
            "Exclusive",
            await ReadMigrationLockModeAsync(firstConnection, firstScope.Transaction));
    }

    [Fact]
    public async Task CommitReleasesTransactionOwnedLock()
    {
        await using var firstConnection = await OpenConnectionAsync();
        await using var firstScope = await SqlServerMigrationTransactionScope.BeginAsync(
            firstConnection,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        await firstScope.CommitAsync(CancellationToken.None);

        await using var secondConnection = await OpenConnectionAsync();
        await using var secondScope = await SqlServerMigrationTransactionScope.BeginAsync(
            secondConnection,
            TimeSpan.Zero,
            CancellationToken.None);

        Assert.Equal(
            "Exclusive",
            await ReadMigrationLockModeAsync(secondConnection, secondScope.Transaction));
    }

    [Fact]
    public async Task RollbackReleasesTransactionOwnedLock()
    {
        await using var firstConnection = await OpenConnectionAsync();
        await using var firstScope = await SqlServerMigrationTransactionScope.BeginAsync(
            firstConnection,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        await firstScope.RollbackAsync(CancellationToken.None);

        await using var secondConnection = await OpenConnectionAsync();
        await using var secondScope = await SqlServerMigrationTransactionScope.BeginAsync(
            secondConnection,
            TimeSpan.Zero,
            CancellationToken.None);

        Assert.Equal(
            "Exclusive",
            await ReadMigrationLockModeAsync(secondConnection, secondScope.Transaction));
    }

    private async Task<SqlConnection> OpenConnectionAsync()
    {
        var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<int> ReadIsolationLevelAsync(
        SqlConnection connection,
        SqlTransaction transaction)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT transaction_isolation_level
            FROM sys.dm_exec_sessions
            WHERE session_id = @@SPID;
            """;
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ReadMigrationLockModeAsync(
        SqlConnection connection,
        SqlTransaction transaction)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT APPLOCK_MODE(
                N'public',
                N'FactoryConnect.SqlMigration',
                N'Transaction');
            """;
        return Convert.ToString(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }
}
