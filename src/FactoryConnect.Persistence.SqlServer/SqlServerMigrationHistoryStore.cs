using System.Collections.Immutable;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlClient.Server;

namespace FactoryConnect.Persistence.SqlServer;

internal interface ISqlMigrationUtcClock
{
    DateTimeOffset UtcNow { get; }
}

internal sealed class SystemSqlMigrationUtcClock : ISqlMigrationUtcClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

internal sealed class SqlServerMigrationHistoryStore
{
    private readonly ISqlMigrationUtcClock _clock;

    public SqlServerMigrationHistoryStore(ISqlMigrationUtcClock clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<ImmutableArray<SqlMigrationHistoryRow>> ReadAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MigrationId, Name, CanonicalChecksum, AppliedAtUtc
            FROM dbo.FactoryConnectMigrationHistory
            ORDER BY MigrationId ASC;
            """;

        var rows = ImmutableArray.CreateBuilder<SqlMigrationHistoryRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new SqlMigrationHistoryRow(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetDateTimeOffset(3));
            SqlMigrationHistoryRowValidator.Validate(row);
            rows.Add(row);
        }

        return rows.ToImmutable();
    }

    public async Task InsertAsync(
        SqlConnection connection,
        SqlMigrationDescriptor migration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(migration);

        var appliedAtUtc = _clock.UtcNow;
        if (appliedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("Migration UTC clock must return offset zero.");
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbo.FactoryConnectMigrationHistory
                (MigrationId, Name, CanonicalChecksum, AppliedAtUtc)
            VALUES
                (@MigrationId, @Name, @CanonicalChecksum, @AppliedAtUtc);
            """;
        command.Parameters.Add(new SqlParameter("@MigrationId", System.Data.SqlDbType.Int) { Value = migration.MigrationId });
        command.Parameters.Add(new SqlParameter("@Name", System.Data.SqlDbType.NVarChar, 128) { Value = migration.Name });
        command.Parameters.Add(new SqlParameter("@CanonicalChecksum", System.Data.SqlDbType.Char, 64) { Value = migration.Sha256Checksum });
        command.Parameters.Add(new SqlParameter("@AppliedAtUtc", System.Data.SqlDbType.DateTimeOffset) { Scale = 7, Value = appliedAtUtc });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
