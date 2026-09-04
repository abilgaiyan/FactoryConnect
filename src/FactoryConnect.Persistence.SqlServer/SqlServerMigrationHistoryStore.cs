using System.Collections.Immutable;
using Microsoft.Data.SqlClient;

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

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "The history store remains an instance boundary because reads and writes are intentionally exposed through one cohesive store abstraction while only writes currently require the injected clock.")]
    public async Task<ImmutableArray<SqlMigrationHistoryRow>> ReadAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
        SqlTransaction transaction,
        SqlMigrationDescriptor migration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(migration);

        var appliedAtUtc = _clock.UtcNow;
        if (appliedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("Migration UTC clock must return offset zero.");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
