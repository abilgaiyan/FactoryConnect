using System.Collections.Immutable;

namespace FactoryConnect.Persistence.SqlServer;

internal readonly record struct SqlObjectName(string SchemaName, string ObjectName);

internal sealed record SqlSchemaDescriptor(ImmutableArray<SqlTableDescriptor> Tables);

internal sealed record SqlTableDescriptor(
    SqlObjectName Name,
    ImmutableArray<SqlColumnDescriptor> Columns,
    SqlPrimaryKeyDescriptor? PrimaryKey,
    ImmutableArray<SqlUniqueConstraintDescriptor> UniqueConstraints,
    ImmutableArray<SqlForeignKeyDescriptor> ForeignKeys,
    ImmutableArray<SqlCheckConstraintDescriptor> CheckConstraints,
    ImmutableArray<SqlIndexDescriptor> Indexes);

internal sealed record SqlColumnDescriptor(
    string Name,
    string SqlType,
    SqlLengthDescriptor? MaxLength,
    byte? Precision,
    byte? Scale,
    bool IsNullable,
    string? Collation,
    SqlIdentityDescriptor? Identity,
    SqlComputedColumnDescriptor? Computed = null);

internal sealed record SqlComputedColumnDescriptor(
    string CanonicalDefinition,
    bool IsPersisted);

internal sealed record SqlLengthDescriptor(int? Value, bool IsMax)
{
    public static SqlLengthDescriptor Bounded(int value) => new(value, IsMax: false);

    public static SqlLengthDescriptor Max { get; } = new(Value: null, IsMax: true);
}

internal sealed record SqlIdentityDescriptor(
    decimal SeedValue,
    decimal IncrementValue,
    bool IsNotForReplication);

internal sealed record SqlPrimaryKeyDescriptor(
    string Name,
    bool IsEnabled,
    SqlIndexStructureDescriptor IndexStructure);

internal sealed record SqlUniqueConstraintDescriptor(
    string Name,
    bool IsEnabled,
    SqlIndexStructureDescriptor IndexStructure);

internal enum SqlReferentialAction
{
    NoAction,
    Cascade,
    SetNull,
    SetDefault
}

internal sealed record SqlForeignKeyDescriptor(
    string Name,
    ImmutableArray<string> Columns,
    SqlObjectName ReferencedTable,
    ImmutableArray<string> ReferencedColumns,
    SqlReferentialAction DeleteAction,
    SqlReferentialAction UpdateAction,
    bool IsEnabled,
    bool IsTrusted,
    bool IsNotForReplication);

internal sealed record SqlCheckConstraintDescriptor(
    string Name,
    string CanonicalDefinition,
    bool IsEnabled,
    bool IsTrusted,
    bool IsNotForReplication);

internal sealed record SqlIndexDescriptor(
    string Name,
    bool IsUnique,
    bool IsEnabled,
    SqlIndexStructureDescriptor IndexStructure);

internal sealed record SqlIndexStructureDescriptor(
    bool IsClustered,
    ImmutableArray<SqlIndexColumnDescriptor> KeyColumns,
    ImmutableArray<string> IncludedColumns,
    string? CanonicalFilterDefinition);

internal enum SqlIndexColumnDirection
{
    Ascending,
    Descending
}

internal sealed record SqlIndexColumnDescriptor(
    string Name,
    SqlIndexColumnDirection Direction,
    int KeyOrdinal);

internal sealed class SqlOwnedObjectRecognitionSet
{
    private readonly ImmutableHashSet<SqlObjectName> _lookup;

    public SqlOwnedObjectRecognitionSet(IEnumerable<SqlObjectName> ownedTables)
    {
        ArgumentNullException.ThrowIfNull(ownedTables);

        OwnedTables = ownedTables
            .Distinct()
            .OrderBy(static item => item.SchemaName, StringComparer.Ordinal)
            .ThenBy(static item => item.ObjectName, StringComparer.Ordinal)
            .ToImmutableArray();
        _lookup = OwnedTables.ToImmutableHashSet();
    }

    public ImmutableArray<SqlObjectName> OwnedTables { get; }

    public bool ContainsRepositoryIdentity(SqlObjectName objectName) => _lookup.Contains(objectName);
}
