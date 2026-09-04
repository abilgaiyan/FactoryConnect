using System.Collections.Immutable;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed class SqlServerMigrationLedgerMetadataReader
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "The ledger metadata reader remains an instance boundary so callers can depend on one cohesive metadata-reader object while its current implementation is stateless.")]
    public async Task<SqlMigrationLedgerObjectState> ResolveObjectAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT o.object_id, o.type
            FROM sys.objects AS o
            INNER JOIN sys.schemas AS s
                ON s.schema_id = o.schema_id
            WHERE s.name = @SchemaName
              AND o.name = @ObjectName;
            """;
        command.Parameters.AddWithValue("@SchemaName", SqlMigrationLedgerContract.SchemaName);
        command.Parameters.AddWithValue("@ObjectName", SqlMigrationLedgerContract.TableName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new SqlMigrationLedgerObjectState(SqlMigrationLedgerObjectKind.Absent, null, null);
        }

        var objectId = reader.GetInt32(0);
        var objectType = reader.GetString(1);
        return string.Equals(objectType, "U", StringComparison.Ordinal)
            ? new SqlMigrationLedgerObjectState(SqlMigrationLedgerObjectKind.UserTable, objectId, objectType)
            : new SqlMigrationLedgerObjectState(SqlMigrationLedgerObjectKind.IncompatibleObject, objectId, objectType);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "The ledger metadata reader remains an instance boundary so callers can depend on one cohesive metadata-reader object while its current implementation is stateless.")]
    public async Task<SqlMigrationLedgerSchemaSnapshot> ReadSchemaAsync(
        SqlConnection connection,
        int objectId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var columns = await ReadColumnsAsync(connection, objectId, cancellationToken);
        var primaryKey = await ReadPrimaryKeyAsync(connection, objectId, cancellationToken);
        var uniqueConstraints = await ReadNamesAsync(connection, "sys.key_constraints", "parent_object_id", objectId, "type = 'UQ'", cancellationToken);
        var foreignKeys = await ReadNamesAsync(connection, "sys.foreign_keys", "parent_object_id", objectId, null, cancellationToken);
        var defaultConstraints = await ReadNamesAsync(connection, "sys.default_constraints", "parent_object_id", objectId, null, cancellationToken);
        var checkConstraints = await ReadNamesAsync(connection, "sys.check_constraints", "parent_object_id", objectId, null, cancellationToken);
        var ordinaryIndexes = await ReadOrdinaryIndexesAsync(connection, objectId, cancellationToken);
        var triggers = await ReadNamesAsync(connection, "sys.triggers", "parent_id", objectId, null, cancellationToken);

        return new SqlMigrationLedgerSchemaSnapshot(
            columns,
            primaryKey,
            uniqueConstraints,
            foreignKeys,
            defaultConstraints,
            checkConstraints,
            ordinaryIndexes,
            triggers);
    }

    private static async Task<ImmutableArray<SqlMigrationLedgerColumnDescriptor>> ReadColumnsAsync(
        SqlConnection connection,
        int objectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                c.name,
                ty.name,
                c.max_length,
                c.scale,
                c.is_nullable,
                c.collation_name
            FROM sys.columns AS c
            INNER JOIN sys.types AS ty
                ON ty.user_type_id = c.user_type_id
            WHERE c.object_id = @ObjectId
            ORDER BY c.column_id;
            """;
        command.Parameters.AddWithValue("@ObjectId", objectId);

        var columns = ImmutableArray.CreateBuilder<SqlMigrationLedgerColumnDescriptor>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var sqlType = reader.GetString(1);
            columns.Add(new SqlMigrationLedgerColumnDescriptor(
                reader.GetString(0),
                sqlType,
                SqlServerSchemaMetadataReader.NormalizeLength(sqlType, reader.GetInt16(2)),
                SqlServerSchemaMetadataReader.NormalizeScale(sqlType, reader.GetByte(3)),
                reader.GetBoolean(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return columns.ToImmutable();
    }

    private static async Task<SqlMigrationLedgerPrimaryKeyDescriptor?> ReadPrimaryKeyAsync(
        SqlConnection connection,
        int objectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                kc.name,
                i.type,
                i.is_disabled,
                ic.key_ordinal,
                ic.is_descending_key,
                c.name
            FROM sys.key_constraints AS kc
            INNER JOIN sys.indexes AS i
                ON i.object_id = kc.parent_object_id
                AND i.index_id = kc.unique_index_id
            INNER JOIN sys.index_columns AS ic
                ON ic.object_id = i.object_id
                AND ic.index_id = i.index_id
            INNER JOIN sys.columns AS c
                ON c.object_id = ic.object_id
                AND c.column_id = ic.column_id
            WHERE kc.parent_object_id = @ObjectId
              AND kc.type = 'PK'
            ORDER BY ic.key_ordinal;
            """;
        command.Parameters.AddWithValue("@ObjectId", objectId);

        var rows = new List<PrimaryKeyRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new PrimaryKeyRow(
                reader.GetString(0),
                reader.GetByte(1),
                reader.GetBoolean(2),
                reader.GetByte(3),
                reader.GetBoolean(4),
                reader.GetString(5)));
        }

        if (rows.Count == 0)
        {
            return null;
        }

        var first = rows[0];
        return new SqlMigrationLedgerPrimaryKeyDescriptor(
            first.Name,
            SqlServerSchemaMetadataReader.MapIndexClusteredness(first.IndexType),
            !first.IsDisabled,
            rows.Select(static row => new SqlIndexColumnDescriptor(
                    row.ColumnName,
                    row.IsDescending ? SqlIndexColumnDirection.Descending : SqlIndexColumnDirection.Ascending,
                    row.Ordinal))
                .ToImmutableArray());
    }

    private static async Task<ImmutableArray<string>> ReadOrdinaryIndexesAsync(
        SqlConnection connection,
        int objectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sys.indexes
            WHERE object_id = @ObjectId
              AND is_primary_key = 0
              AND is_unique_constraint = 0
              AND name IS NOT NULL
            ORDER BY name;
            """;
        command.Parameters.AddWithValue("@ObjectId", objectId);
        return await ReadStringColumnAsync(command, cancellationToken);
    }

    private static async Task<ImmutableArray<string>> ReadNamesAsync(
        SqlConnection connection,
        string catalogView,
        string objectIdColumn,
        int objectId,
        string? extraPredicate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM {catalogView} WHERE {objectIdColumn} = @ObjectId" +
            (extraPredicate is null ? string.Empty : $" AND {extraPredicate}") +
            " ORDER BY name;";
        command.Parameters.AddWithValue("@ObjectId", objectId);
        return await ReadStringColumnAsync(command, cancellationToken);
    }

    private static async Task<ImmutableArray<string>> ReadStringColumnAsync(
        SqlCommand command,
        CancellationToken cancellationToken)
    {
        var values = ImmutableArray.CreateBuilder<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(reader.GetString(0));
        }

        return values.ToImmutable();
    }

    private sealed record PrimaryKeyRow(
        string Name,
        byte IndexType,
        bool IsDisabled,
        int Ordinal,
        bool IsDescending,
        string ColumnName);
}

internal static class SqlMigrationLedgerSchemaValidator
{
    public static void Validate(SqlMigrationLedgerSchemaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!snapshot.Columns.SequenceEqual(SqlMigrationLedgerContract.Columns) ||
            snapshot.PrimaryKey != SqlMigrationLedgerContract.PrimaryKey ||
            !snapshot.UniqueConstraints.IsEmpty ||
            !snapshot.ForeignKeys.IsEmpty ||
            !snapshot.DefaultConstraints.IsEmpty ||
            !snapshot.CheckConstraints.IsEmpty ||
            !snapshot.OrdinaryIndexes.IsEmpty ||
            !snapshot.Triggers.IsEmpty)
        {
            throw new InvalidOperationException("FactoryConnect migration ledger schema is invalid.");
        }
    }
}
