using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed class SqlServerSchemaMetadataReader
{
    private readonly SqlServerOwnedObjectResolver _resolver = new();

    public async Task<SqlSchemaDescriptor> ReadFactoryConnectOwnedSchemaAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var resolvedObjects = await _resolver.ResolveAsync(
            connection,
            SqlRepositorySchemaAuthority.OwnedObjects,
            cancellationToken);
        var repositoryIdentityByObjectId = resolvedObjects.ToDictionary(
            static item => item.ObjectId,
            static item => item.RepositoryIdentity);
        var tables = ImmutableArray.CreateBuilder<SqlTableDescriptor>(resolvedObjects.Length);

        foreach (var resolvedObject in resolvedObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            tables.Add(await ReadTableAsync(
                connection,
                resolvedObject,
                repositoryIdentityByObjectId,
                cancellationToken));
        }

        return new SqlSchemaDescriptor(tables
            .OrderBy(static table => table.Name.SchemaName, StringComparer.Ordinal)
            .ThenBy(static table => table.Name.ObjectName, StringComparer.Ordinal)
            .ToImmutableArray());
    }

    private static async Task<SqlTableDescriptor> ReadTableAsync(
        SqlConnection connection,
        SqlResolvedObject resolvedObject,
        IReadOnlyDictionary<int, SqlObjectName> repositoryIdentityByObjectId,
        CancellationToken cancellationToken)
    {
        var columns = await ReadColumnsAsync(connection, resolvedObject.ObjectId, cancellationToken);
        var keys = await ReadKeyConstraintsAsync(connection, resolvedObject.ObjectId, cancellationToken);
        var foreignKeys = await ReadForeignKeysAsync(
            connection,
            resolvedObject.ObjectId,
            repositoryIdentityByObjectId,
            cancellationToken);
        var checks = await ReadCheckConstraintsAsync(connection, resolvedObject.ObjectId, cancellationToken);
        var indexes = await ReadOrdinaryIndexesAsync(connection, resolvedObject.ObjectId, cancellationToken);

        return new SqlTableDescriptor(
            resolvedObject.RepositoryIdentity,
            columns,
            keys.SingleOrDefault(static key => key.ConstraintType == "PK")?.PrimaryKey,
            keys.Where(static key => key.ConstraintType == "UQ")
                .Select(static key => key.UniqueConstraint!)
                .ToImmutableArray(),
            foreignKeys,
            checks,
            indexes);
    }

    private static async Task<ImmutableArray<SqlColumnDescriptor>> ReadColumnsAsync(
        SqlConnection connection,
        int objectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                c.name,
                c.column_id,
                ty.name AS SqlType,
                c.max_length,
                c.precision,
                c.scale,
                c.is_nullable,
                c.collation_name,
                ic.seed_value,
                ic.increment_value,
                ic.is_not_for_replication
            FROM sys.columns AS c
            INNER JOIN sys.types AS ty
                ON ty.user_type_id = c.user_type_id
            LEFT JOIN sys.identity_columns AS ic
                ON ic.object_id = c.object_id
                AND ic.column_id = c.column_id
            WHERE c.object_id = @ObjectId
            ORDER BY c.column_id;
            """;
        command.Parameters.AddWithValue("@ObjectId", objectId);

        var result = ImmutableArray.CreateBuilder<SqlColumnDescriptor>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var sqlType = reader.GetString(2);
            var identity = reader.IsDBNull(8)
                ? null
                : new SqlIdentityDescriptor(
                    Convert.ToDecimal(reader.GetValue(8), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(9), CultureInfo.InvariantCulture),
                    reader.GetBoolean(10));

            result.Add(new SqlColumnDescriptor(
                reader.GetString(0),
                reader.GetInt32(1),
                sqlType,
                NormalizeLength(sqlType, reader.GetInt16(3)),
                NormalizePrecision(sqlType, reader.GetByte(4)),
                NormalizeScale(sqlType, reader.GetByte(5)),
                reader.GetBoolean(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                identity));
        }

        return result.ToImmutable();
    }

    private static async Task<ImmutableArray<KeyConstraintRead>> ReadKeyConstraintsAsync(
        SqlConnection connection,
        int objectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                kc.name AS ConstraintName,
                kc.type AS ConstraintType,
                i.type AS IndexType,
                i.filter_definition,
                ic.key_ordinal,
                ic.index_column_id,
                ic.is_included_column,
                ic.is_descending_key,
                c.name AS ColumnName
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
            ORDER BY kc.name, ic.is_included_column, ic.key_ordinal, ic.index_column_id;
            """;
        command.Parameters.AddWithValue("@ObjectId", objectId);

        var rows = new List<IndexRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new IndexRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetByte(2) == 1,
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetByte(4),
                reader.GetInt32(5),
                reader.GetBoolean(6),
                reader.GetBoolean(7),
                reader.GetString(8),
                IsUnique: true,
                IsEnabled: true));
        }

        return rows
            .GroupBy(static row => (row.Name, row.ConstraintType))
            .OrderBy(static group => group.Key.Name, StringComparer.Ordinal)
            .Select(static group => CreateKeyConstraint(group.Key.ConstraintType, group.ToArray()))
            .ToImmutableArray();
    }

    private static async Task<ImmutableArray<SqlForeignKeyDescriptor>> ReadForeignKeysAsync(
        SqlConnection connection,
        int objectId,
        IReadOnlyDictionary<int, SqlObjectName> repositoryIdentityByObjectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                fk.name,
                fk.referenced_object_id,
                rs.name AS ReferencedSchemaName,
                rt.name AS ReferencedTableName,
                fk.delete_referential_action,
                fk.update_referential_action,
                fk.is_disabled,
                fk.is_not_trusted,
                fk.is_not_for_replication,
                fkc.constraint_column_id,
                pc.name AS ParentColumnName,
                rc.name AS ReferencedColumnName
            FROM sys.foreign_keys AS fk
            INNER JOIN sys.foreign_key_columns AS fkc
                ON fkc.constraint_object_id = fk.object_id
            INNER JOIN sys.columns AS pc
                ON pc.object_id = fk.parent_object_id
                AND pc.column_id = fkc.parent_column_id
            INNER JOIN sys.columns AS rc
                ON rc.object_id = fk.referenced_object_id
                AND rc.column_id = fkc.referenced_column_id
            INNER JOIN sys.tables AS rt
                ON rt.object_id = fk.referenced_object_id
            INNER JOIN sys.schemas AS rs
                ON rs.schema_id = rt.schema_id
            WHERE fk.parent_object_id = @ObjectId
            ORDER BY fk.name, fkc.constraint_column_id;
            """;
        command.Parameters.AddWithValue("@ObjectId", objectId);

        var rows = new List<ForeignKeyRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ForeignKeyRow(
                reader.GetString(0),
                reader.GetInt32(1),
                new SqlObjectName(reader.GetString(2), reader.GetString(3)),
                ToReferentialAction(reader.GetByte(4)),
                ToReferentialAction(reader.GetByte(5)),
                !reader.GetBoolean(6),
                !reader.GetBoolean(7),
                reader.GetBoolean(8),
                reader.GetInt32(9),
                reader.GetString(10),
                reader.GetString(11)));
        }

        return rows
            .GroupBy(static row => row.Name, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var ordered = group.OrderBy(static row => row.Ordinal).ToArray();
                var first = ordered[0];
                var referencedTable = repositoryIdentityByObjectId.TryGetValue(first.ReferencedObjectId, out var repositoryIdentity)
                    ? repositoryIdentity
                    : first.CatalogReferencedTable;
                return new SqlForeignKeyDescriptor(
                    first.Name,
                    ordered.Select(static row => row.ParentColumn).ToImmutableArray(),
                    referencedTable,
                    ordered.Select(static row => row.ReferencedColumn).ToImmutableArray(),
                    first.DeleteAction,
                    first.UpdateAction,
                    first.IsEnabled,
                    first.IsTrusted,
                    first.IsNotForReplication);
            })
            .ToImmutableArray();
    }

    private static async Task<ImmutableArray<SqlCheckConstraintDescriptor>> ReadCheckConstraintsAsync(
        SqlConnection connection,
        int objectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                name,
                definition,
                is_disabled,
                is_not_trusted,
                is_not_for_replication
            FROM sys.check_constraints
            WHERE parent_object_id = @ObjectId
            ORDER BY name;
            """;
        command.Parameters.AddWithValue("@ObjectId", objectId);

        var result = ImmutableArray.CreateBuilder<SqlCheckConstraintDescriptor>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new SqlCheckConstraintDescriptor(
                reader.GetString(0),
                reader.GetString(1),
                !reader.GetBoolean(2),
                !reader.GetBoolean(3),
                reader.GetBoolean(4)));
        }

        return result.ToImmutable();
    }

    private static async Task<ImmutableArray<SqlIndexDescriptor>> ReadOrdinaryIndexesAsync(
        SqlConnection connection,
        int objectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                i.name,
                i.is_unique,
                i.is_disabled,
                i.type,
                i.filter_definition,
                ic.key_ordinal,
                ic.index_column_id,
                ic.is_included_column,
                ic.is_descending_key,
                c.name AS ColumnName
            FROM sys.indexes AS i
            INNER JOIN sys.index_columns AS ic
                ON ic.object_id = i.object_id
                AND ic.index_id = i.index_id
            INNER JOIN sys.columns AS c
                ON c.object_id = ic.object_id
                AND c.column_id = ic.column_id
            WHERE i.object_id = @ObjectId
                AND i.index_id > 0
                AND i.is_primary_key = 0
                AND i.is_unique_constraint = 0
                AND i.is_hypothetical = 0
            ORDER BY i.name, ic.is_included_column, ic.key_ordinal, ic.index_column_id;
            """;
        command.Parameters.AddWithValue("@ObjectId", objectId);

        var rows = new List<IndexRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new IndexRow(
                reader.GetString(0),
                ConstraintType: string.Empty,
                reader.GetByte(3) == 1,
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetByte(5),
                reader.GetInt32(6),
                reader.GetBoolean(7),
                reader.GetBoolean(8),
                reader.GetString(9),
                reader.GetBoolean(1),
                !reader.GetBoolean(2)));
        }

        return rows
            .GroupBy(static row => row.Name, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(static group =>
            {
                var rows = group.ToArray();
                var first = rows[0];
                return new SqlIndexDescriptor(
                    first.Name,
                    first.IsUnique,
                    first.IsEnabled,
                    CreateIndexStructure(rows));
            })
            .ToImmutableArray();
    }

    internal static SqlLengthDescriptor? NormalizeLength(string sqlType, short catalogMaxLength)
    {
        if (sqlType is not ("binary" or "char" or "nchar" or "nvarchar" or "varbinary" or "varchar"))
        {
            return null;
        }

        if (catalogMaxLength == -1)
        {
            return SqlLengthDescriptor.Max;
        }

        var semanticLength = sqlType is "nchar" or "nvarchar"
            ? catalogMaxLength / 2
            : catalogMaxLength;
        return SqlLengthDescriptor.Bounded(semanticLength);
    }

    internal static byte? NormalizePrecision(string sqlType, byte catalogPrecision) =>
        sqlType is "decimal" or "numeric" ? catalogPrecision : null;

    internal static byte? NormalizeScale(string sqlType, byte catalogScale) =>
        sqlType is "decimal" or "numeric" or "datetime2" or "datetimeoffset" or "time"
            ? catalogScale
            : null;

    private static KeyConstraintRead CreateKeyConstraint(string constraintType, IndexRow[] rows)
    {
        var first = rows[0];
        var structure = CreateIndexStructure(rows);
        return constraintType switch
        {
            "PK" => new KeyConstraintRead(
                constraintType,
                new SqlPrimaryKeyDescriptor(first.Name, structure),
                null),
            "UQ" => new KeyConstraintRead(
                constraintType,
                null,
                new SqlUniqueConstraintDescriptor(first.Name, structure)),
            _ => throw new InvalidOperationException($"Unsupported SQL key constraint type '{constraintType}'.")
        };
    }

    private static SqlIndexStructureDescriptor CreateIndexStructure(IndexRow[] rows)
    {
        var first = rows[0];
        return new SqlIndexStructureDescriptor(
            first.IsClustered,
            rows.Where(static row => !row.IsIncluded)
                .OrderBy(static row => row.KeyOrdinal)
                .Select(static row => new SqlIndexColumnDescriptor(
                    row.ColumnName,
                    row.IsDescending ? SqlIndexColumnDirection.Descending : SqlIndexColumnDirection.Ascending,
                    row.KeyOrdinal))
                .ToImmutableArray(),
            rows.Where(static row => row.IsIncluded)
                .OrderBy(static row => row.IndexColumnOrdinal)
                .Select(static row => row.ColumnName)
                .ToImmutableArray(),
            first.FilterDefinition);
    }

    private static SqlReferentialAction ToReferentialAction(byte value) => value switch
    {
        0 => SqlReferentialAction.NoAction,
        1 => SqlReferentialAction.Cascade,
        2 => SqlReferentialAction.SetNull,
        3 => SqlReferentialAction.SetDefault,
        _ => throw new InvalidOperationException($"Unknown SQL Server referential action '{value}'.")
    };

    private sealed record IndexRow(
        string Name,
        string ConstraintType,
        bool IsClustered,
        string? FilterDefinition,
        int KeyOrdinal,
        int IndexColumnOrdinal,
        bool IsIncluded,
        bool IsDescending,
        string ColumnName,
        bool IsUnique,
        bool IsEnabled);

    private sealed record ForeignKeyRow(
        string Name,
        int ReferencedObjectId,
        SqlObjectName CatalogReferencedTable,
        SqlReferentialAction DeleteAction,
        SqlReferentialAction UpdateAction,
        bool IsEnabled,
        bool IsTrusted,
        bool IsNotForReplication,
        int Ordinal,
        string ParentColumn,
        string ReferencedColumn);

    private sealed record KeyConstraintRead(
        string ConstraintType,
        SqlPrimaryKeyDescriptor? PrimaryKey,
        SqlUniqueConstraintDescriptor? UniqueConstraint);
}
