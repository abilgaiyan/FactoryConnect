using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerMigrationHistoryStoreIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerMigrationHistoryStoreIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task HistoryReaderOrdersByMigrationIdIndependentOfInsertionOrder()
    {
        await using var connection = await OpenConnectionAsync();
        await RecreateExactLedgerAsync(connection);
        var catalog = SqlMigrationCatalog.Load();
        var first = catalog.Migrations[0];
        var second = catalog.Migrations[1];
        await InsertRawAsync(connection, second.MigrationId, second.Name, second.Sha256Checksum, UtcTime());
        await InsertRawAsync(connection, first.MigrationId, first.Name, first.Sha256Checksum, UtcTime());
        await using var transaction = connection.BeginTransaction();
        var store = new SqlServerMigrationHistoryStore(new FixedUtcClock(UtcTime()));

        var history = await store.ReadAsync(connection, transaction, CancellationToken.None);

        Assert.Equal([first.MigrationId, second.MigrationId], history.Select(static row => row.MigrationId));
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task LowercaseChecksumIsRejectedThroughDatabaseReader()
    {
        await using var connection = await OpenConnectionAsync();
        await RecreateExactLedgerAsync(connection);
        var migration = SqlMigrationCatalog.Load().Migrations[0];
        await InsertRawAsync(
            connection,
            migration.MigrationId,
            migration.Name,
            migration.Sha256Checksum.ToLowerInvariant(),
            UtcTime());
        await using var transaction = connection.BeginTransaction();
        var store = new SqlServerMigrationHistoryStore(new FixedUtcClock(UtcTime()));

        await Assert.ThrowsAsync<SqlMigrationHistoryException>(
            () => store.ReadAsync(connection, transaction, CancellationToken.None));
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task NonZeroOffsetIsRejectedThroughDatabaseReader()
    {
        await using var connection = await OpenConnectionAsync();
        await RecreateExactLedgerAsync(connection);
        var migration = SqlMigrationCatalog.Load().Migrations[0];
        await InsertRawAsync(
            connection,
            migration.MigrationId,
            migration.Name,
            migration.Sha256Checksum,
            new DateTimeOffset(2026, 9, 4, 12, 30, 0, TimeSpan.FromHours(5.5)));
        await using var transaction = connection.BeginTransaction();
        var store = new SqlServerMigrationHistoryStore(new FixedUtcClock(UtcTime()));

        await Assert.ThrowsAsync<SqlMigrationHistoryException>(
            () => store.ReadAsync(connection, transaction, CancellationToken.None));
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task InsertAndReadParticipateInExplicitLocalTransaction()
    {
        await using var connection = await OpenConnectionAsync();
        await RecreateExactLedgerAsync(connection);
        var migration = SqlMigrationCatalog.Load().Migrations[0];
        var clockValue = UtcTime();
        var store = new SqlServerMigrationHistoryStore(new FixedUtcClock(clockValue));
        await using (var transaction = connection.BeginTransaction())
        {
            await store.InsertAsync(connection, transaction, migration, CancellationToken.None);
            var history = await store.ReadAsync(connection, transaction, CancellationToken.None);

            var row = Assert.Single(history);
            Assert.Equal(migration.MigrationId, row.MigrationId);
            Assert.Equal(migration.Name, row.Name);
            Assert.Equal(migration.Sha256Checksum, row.CanonicalChecksum);
            Assert.Equal(clockValue, row.AppliedAtUtc);
            await transaction.RollbackAsync();
        }

        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM dbo.FactoryConnectMigrationHistory;";
        Assert.Equal(0, Convert.ToInt32(await countCommand.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
    }

    private async Task<SqlConnection> OpenConnectionAsync()
    {
        var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        return connection;
    }

    private static async Task RecreateExactLedgerAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF OBJECT_ID(N'dbo.FactoryConnectMigrationHistory', N'U') IS NOT NULL
                DROP TABLE dbo.FactoryConnectMigrationHistory;

            CREATE TABLE dbo.FactoryConnectMigrationHistory
            (
                MigrationId int NOT NULL,
                Name nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                CanonicalChecksum char(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                AppliedAtUtc datetimeoffset(7) NOT NULL,
                CONSTRAINT PK_FactoryConnectMigrationHistory
                    PRIMARY KEY CLUSTERED (MigrationId ASC)
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertRawAsync(
        SqlConnection connection,
        int migrationId,
        string name,
        string checksum,
        DateTimeOffset appliedAtUtc)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbo.FactoryConnectMigrationHistory
                (MigrationId, Name, CanonicalChecksum, AppliedAtUtc)
            VALUES
                (@MigrationId, @Name, @CanonicalChecksum, @AppliedAtUtc);
            """;
        command.Parameters.Add(new SqlParameter("@MigrationId", System.Data.SqlDbType.Int) { Value = migrationId });
        command.Parameters.Add(new SqlParameter("@Name", System.Data.SqlDbType.NVarChar, 128) { Value = name });
        command.Parameters.Add(new SqlParameter("@CanonicalChecksum", System.Data.SqlDbType.Char, 64) { Value = checksum });
        command.Parameters.Add(new SqlParameter("@AppliedAtUtc", System.Data.SqlDbType.DateTimeOffset) { Scale = 7, Value = appliedAtUtc });
        await command.ExecuteNonQueryAsync();
    }

    private static DateTimeOffset UtcTime() =>
        new(2026, 9, 4, 7, 0, 0, TimeSpan.Zero);

    private sealed class FixedUtcClock : ISqlMigrationUtcClock
    {
        public FixedUtcClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; }
    }
}
