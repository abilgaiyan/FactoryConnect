using System.Collections.Immutable;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed class SqlServerRuntimeMigrationHistoryReader
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "The raw runtime history reader remains an instance boundary for the compatibility verifier while its current implementation is stateless.")]
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
            rows.Add(new SqlMigrationHistoryRow(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetDateTimeOffset(3)));
        }

        return rows.ToImmutable();
    }
}
