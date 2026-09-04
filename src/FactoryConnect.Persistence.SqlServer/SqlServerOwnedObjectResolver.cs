using System.Collections.Immutable;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed record SqlResolvedObject(
    SqlObjectName RepositoryIdentity,
    SqlObjectName CatalogIdentity,
    int ObjectId);

internal static class SqlServerOwnedObjectResolver
{
    public static Task<ImmutableArray<SqlResolvedObject>> ResolveAsync(
        SqlConnection connection,
        SqlOwnedObjectRecognitionSet recognitionSet,
        CancellationToken cancellationToken) =>
        ResolveCoreAsync(connection, transaction: null, recognitionSet, cancellationToken);

    public static Task<ImmutableArray<SqlResolvedObject>> ResolveInTransactionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        SqlOwnedObjectRecognitionSet recognitionSet,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return ResolveCoreAsync(connection, transaction, recognitionSet, cancellationToken);
    }

    private static async Task<ImmutableArray<SqlResolvedObject>> ResolveCoreAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        SqlOwnedObjectRecognitionSet recognitionSet,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(recognitionSet);

        var resolved = ImmutableArray.CreateBuilder<SqlResolvedObject>();
        foreach (var repositoryIdentity in recognitionSet.OwnedTables)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT
                    t.object_id,
                    s.name AS SchemaName,
                    t.name AS TableName
                FROM sys.tables AS t
                INNER JOIN sys.schemas AS s
                    ON s.schema_id = t.schema_id
                WHERE t.object_id = OBJECT_ID(
                    QUOTENAME(@SchemaName) + N'.' + QUOTENAME(@TableName),
                    N'U');
                """;
            command.Parameters.AddWithValue("@SchemaName", repositoryIdentity.SchemaName);
            command.Parameters.AddWithValue("@TableName", repositoryIdentity.ObjectName);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                continue;
            }

            resolved.Add(new SqlResolvedObject(
                repositoryIdentity,
                new SqlObjectName(reader.GetString(1), reader.GetString(2)),
                reader.GetInt32(0)));
        }

        return resolved
            .OrderBy(static item => item.RepositoryIdentity.SchemaName, StringComparer.Ordinal)
            .ThenBy(static item => item.RepositoryIdentity.ObjectName, StringComparer.Ordinal)
            .ToImmutableArray();
    }
}
