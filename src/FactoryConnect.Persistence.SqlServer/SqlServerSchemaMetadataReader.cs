using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed class SqlServerSchemaMetadataReader
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "The reader remains an instance boundary so callers can depend on a reader object while its current implementation is stateless.")]
    public async Task<SqlSchemaDescriptor> ReadFactoryConnectOwnedSchemaAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var resolvedObjects = await SqlServerOwnedObjectResolver.ResolveAsync(
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
            keys.PrimaryKey,
            keys.UniqueConstraints,
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

        var columns = ImmutableArray.CreateBuilder<SqlColumnDescriptor>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var sqlType = reader.GetString(1);
            var identity = reader.IsDBNull(7)
                ? null
                : new SqlIdentityDescriptor(
                    Convert.ToDecimal(reader.GetValue(7), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(8), CultureInfo.InvariantCulture),
                    reader.GetBoolean(9));

            columns.Add(new SqlColumnDescriptor(
                reader.GetString(0),
                sqlType,
                NormalizeLength(sqlType, reader.GetInt16(2)),
                NormalizePrecision(sqlType, reader.GetByte(3)),
                NormalizeScale(sqlType, reader.GetByte(4)),
                reader.GetBoolean(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                identity));
        }

        return columns.ToImmutable();
    }

    private static async Task<(SqlPrimaryKeyDescriptor? PrimaryKey, ImmutableArray<SqlUniqueConstraintDescriptor> UniqueConstraints)> ReadKeyConstraintsAsync(
        SqlConnection connection,
        int objectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                kc.name,
                kc.type,
                i.type,
                i.is_disabled,
                i.filter_definition,
                ic.key_ordinal,
                ic.is_descending_key,
                ic.is_included_column,
                ic.index_column_id,
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
            ORDER BY kc.name, ic.index_column_id;
            """;
        command.Parameters.AddWithValue("@ObjectId", objectId);

        var rows = new List<IndexRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new IndexRow(
                reader.GetString(0),
                reader.GetString(1),
                IsUnique: true,
                reader.GetBoolean(3),
                reader.GetByte(2),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetByte(5),
                reader.GetBoolean(6),
                reader.GetBoolean(7),
                reader.GetInt32(8),
                reader.GetString(9)));
        }

        SqlPrimaryKeyDescriptor? primaryKey = null;
        var uniques = ImmutableArray.CreateBuilder<SqlUniqueConstraintDescriptor>();
        foreach (var group in rows.GroupBy(static row => (
            row.Name,
            row.ConstraintType,
            row.IsDisabled,
            row.IndexType,
            row.FilterDefinition)))
        {
            var structure = CreateIndexStructure(group.Key.IndexType, group, group.Key.FilterDefinition);
            if (string.Equals(group.Key.ConstraintType, "PK", StringComparison.Ordinal))
            {
                primaryKey = new SqlPrimaryKeyDescriptor(
                    group.Key.Name,
                    IsEnabled: !group.Key.IsDisabled,
                    structure);
            }
            else
            {
                uniques.Add(new SqlUniqueConstraintDescriptor(
                    group.Key.Name,
                    IsEnabled: !group.Key.IsDisabled,
                    structure));
            }
        }

        return (
            primaryKey,
            uniques.OrderBy(static item => item.Name, StringComparer.Ordinal).ToImmutableArray());
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
                fk.delete_referential_action,
                fk.update_referential_action,
                fk.is_disabled,
                fk.is_not_trusted,
                fk.is_not_for_replication,
                fkc.constraint_column_id,
                pc.name AS ParentColumn,
                rc.name AS ReferencedColumn,
                rs.name AS ReferencedSchema,
                rt.name AS ReferencedTable
            FROM sys.foreign_keys AS fk
            INNER JOIN sys.foreign_key_columns AS fkc
                ON fkc.constraint_object_id = fk.object_id
            INNER JOIN sys.columns AS pc
                ON pc.object_id = fkc.parent_object_id
                AND pc.column_id = fkc.parent_column_id
            INNER JOIN sys.columns AS rc
                ON rc.object_id = fkc.referenced_object_id
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
                reader.GetByte(2),
                reader.GetByte(3),
                reader.GetBoolean(4),
                reader.GetBoolean(5),
                reader.GetBoolean(6),
                reader.GetInt32(7),
                reader.GetString(8),
                reader.GetString(9),
                new SqlObjectName(reader.GetString(10), reader.GetString(11))));
        }

        return rows
            .GroupBy(static row => row.Name, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                var referencedTable = repositoryIdentityByObjectId.TryGetValue(first.ReferencedObjectId, out var repositoryIdentity)
                    ? repositoryIdentity
                    : first.CatalogReferencedTable;
                var ordered = group.OrderBy(static row => row.Ordinal).ToArray();
                return new SqlForeignKeyDescriptor(
                    first.Name,
                    ordered.Select(static row => row.ParentColumn).ToImmutableArray(),
                    referencedTable,
                    ordered.Select(static row => row.ReferencedColumn).ToImmutableArray(),
                    MapReferentialAction(first.DeleteAction),
                    MapReferentialAction(first.UpdateAction),
                    !first.IsDisabled,
                    !first.IsNotTrusted,
                    first.IsNotForReplication);
            })
            .OrderBy(static item => item.Name, StringComparer.Ordinal)
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

        var checks = ImmutableArray.CreateBuilder<SqlCheckConstraintDescriptor>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            checks.Add(new SqlCheckConstraintDescriptor(
                reader.GetString(0),
                reader.GetString(1),
                !reader.GetBoolean(2),
                !reader.GetBoolean(3),
                reader.GetBoolean(4)));
        }

        return checks.ToImmutable();
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
                ic.is_descending_key,
                ic.is_included_column,
                ic.index_column_id,
                c.name AS ColumnName
            FROM sys.indexes AS i
            INNER JOIN sys.index_columns AS ic
                ON ic.object_id = i.object_id
                AND ic.index_id = i.index_id
            INNER JOIN sys.columns AS c
                ON c.object_id = ic.object_id
                AND c.column_id = ic.column_id
            WHERE i.object_id = @ObjectId
              AND i.is_primary_key = 0
              AND i.is_unique_constraint = 0
              AND i.name IS NOT NULL
            ORDER BY i.name, ic.index_column_id;
            """;
        command.Parameters.AddWithValue("@ObjectId", objectId);

        var rows = new List<IndexRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new IndexRow(
                reader.GetString(0),
                null,
                reader.GetBoolean(1),
                reader.GetBoolean(2),
                reader.GetByte(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetByte(5),
                reader.GetBoolean(6),
                reader.GetBoolean(7),
                reader.GetInt32(8),
                reader.GetString(9)));
        }

        return rows
            .GroupBy(static row => (row.Name, row.IsUnique, row.IsDisabled, row.IndexType, row.FilterDefinition))
            .Select(group => new SqlIndexDescriptor(
                group.Key.Name,
                group.Key.IsUnique,
                !group.Key.IsDisabled,
                CreateIndexStructure(group.Key.IndexType, group, group.Key.FilterDefinition)))
            .OrderBy(static item => item.Name, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static SqlIndexStructureDescriptor CreateIndexStructure(
        byte indexType,
        IEnumerable<IndexRow> rows,
        string? filterDefinition)
    {
        var isClustered = MapIndexClusteredness(indexType);
        var ordered = rows.OrderBy(static row => row.IndexColumnId).ToArray();
        var keys = ordered
            .Where(static row => !row.IsIncluded)
            .OrderBy(static row => row.KeyOrdinal)
            .Select(static row => new SqlIndexColumnDescriptor(
                row.ColumnName,
                row.IsDescending ? SqlIndexColumnDirection.Descending : SqlIndexColumnDirection.Ascending,
                row.KeyOrdinal))
            .ToImmutableArray();
        var included = ordered
            .Where(static row => row.IsIncluded)
            .Select(static row => row.ColumnName)
            .ToImmutableArray();

        return new SqlIndexStructureDescriptor(
            isClustered,
            keys,
            included,
            filterDefinition);
    }

    internal static bool MapIndexClusteredness(byte indexType) => indexType switch
    {
        1 => true,
        2 => false,
        _ => throw new InvalidOperationException(
            $"Unsupported SQL Server index type '{indexType}'.")
    };

    internal static SqlLengthDescriptor? NormalizeLength(string sqlType, short catalogMaxLength)
    {
        if (catalogMaxLength == -1)
        {
            return SqlLengthDescriptor.Max;
        }

        return sqlType switch
        {
            "char" or "varchar" or "binary" or "varbinary" => SqlLengthDescriptor.Bounded(catalogMaxLength),
            "nchar" or "nvarchar" => SqlLengthDescriptor.Bounded(catalogMaxLength / 2),
            _ => null
        };
    }

    internal static byte? NormalizePrecision(string sqlType, byte catalogPrecision) =>
        sqlType is "decimal" or "numeric" ? catalogPrecision : null;

    internal static byte? NormalizeScale(string sqlType, byte catalogScale) =>
        sqlType is "decimal" or "numeric" or "datetime2" or "datetimeoffset" or "time"
            ? catalogScale
            : null;

    private static SqlReferentialAction MapReferentialAction(byte action) => action switch
    {
        0 => SqlReferentialAction.NoAction,
        1 => SqlReferentialAction.Cascade,
        2 => SqlReferentialAction.SetNull,
        3 => SqlReferentialAction.SetDefault,
        _ => throw new InvalidOperationException($"Unsupported SQL Server referential action '{action}'.")
    };

    private sealed record IndexRow(
        string Name,
        string? ConstraintType,
        bool IsUnique,
        bool IsDisabled,
        byte IndexType,
        string? FilterDefinition,
        int KeyOrdinal,
        bool IsDescending,
        bool IsIncluded,
        int IndexColumnId,
        string ColumnName);

    private sealed record ForeignKeyRow(
        string Name,
        int ReferencedObjectId,
        byte DeleteAction,
        byte UpdateAction,
        bool IsDisabled,
        bool IsNotTrusted,
        bool IsNotForReplication,
        int Ordinal,
        string ParentColumn,
        string ReferencedColumn,
        SqlObjectName CatalogReferencedTable);
}
