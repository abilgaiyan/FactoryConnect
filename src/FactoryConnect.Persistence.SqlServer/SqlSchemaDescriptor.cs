using System.Collections.Immutable;

namespace FactoryConnect.Persistence.SqlServer;

internal readonly record struct SqlObjectName(string SchemaName, string ObjectName);

internal enum SqlIndexColumnDirection
{
    Ascending = 0,
    Descending = 1
}

internal enum SqlReferentialAction
{
    NoAction = 0,
    Cascade = 1,
    SetNull = 2,
    SetDefault = 3
}

internal readonly record struct SqlLengthDescriptor
{
    private SqlLengthDescriptor(int? value)
    {
        if (value is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "SQL length must be positive when specified.");
        }

        Value = value;
    }

    public int? Value { get; }

    public bool IsMax => Value is null;

    public static SqlLengthDescriptor Bounded(int value) => new(value);

    public static SqlLengthDescriptor Max { get; } = new(null);
}

internal sealed record SqlIdentityDescriptor(
    decimal SeedValue,
    decimal IncrementValue,
    bool IsNotForReplication);

internal sealed record SqlColumnDescriptor(
    string Name,
    int Ordinal,
    string SqlType,
    SqlLengthDescriptor? MaxLength,
    byte? Precision,
    byte? Scale,
    bool IsNullable,
    string? Collation,
    SqlIdentityDescriptor? Identity);

internal sealed record SqlIndexColumnDescriptor(
    string Name,
    SqlIndexColumnDirection Direction,
    int Ordinal);

internal sealed record SqlIndexStructureDescriptor(
    bool IsClustered,
    ImmutableArray<SqlIndexColumnDescriptor> KeyColumns,
    ImmutableArray<string> IncludedColumns,
    string? CanonicalFilterDefinition);

internal sealed record SqlPrimaryKeyDescriptor(
    string Name,
    SqlIndexStructureDescriptor IndexStructure);

internal sealed record SqlUniqueConstraintDescriptor(
    string Name,
    SqlIndexStructureDescriptor IndexStructure);

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

internal sealed record SqlTableDescriptor(
    SqlObjectName Name,
    ImmutableArray<SqlColumnDescriptor> Columns,
    SqlPrimaryKeyDescriptor? PrimaryKey,
    ImmutableArray<SqlUniqueConstraintDescriptor> UniqueConstraints,
    ImmutableArray<SqlForeignKeyDescriptor> ForeignKeys,
    ImmutableArray<SqlCheckConstraintDescriptor> CheckConstraints,
    ImmutableArray<SqlIndexDescriptor> Indexes);

internal sealed record SqlSchemaDescriptor(
    ImmutableArray<SqlTableDescriptor> Tables);
