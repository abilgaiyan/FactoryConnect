using System.Collections.Immutable;

namespace FactoryConnect.Persistence.SqlServer;

internal enum SqlSchemaDifferenceKind
{
    MissingOwnedTable,
    UnexpectedOwnedTable,
    MissingColumn,
    UnexpectedColumn,
    ColumnTypeMismatch,
    ColumnLengthMismatch,
    ColumnPrecisionMismatch,
    ColumnScaleMismatch,
    ColumnNullabilityMismatch,
    ColumnCollationMismatch,
    ColumnIdentityMismatch,
    PrimaryKeyMismatch,
    UniqueConstraintMismatch,
    ForeignKeyMismatch,
    CheckConstraintMismatch,
    MissingIndex,
    UnexpectedIndex,
    IndexMismatch
}

internal sealed record SqlSchemaDifference(
    SqlSchemaDifferenceKind Kind,
    SqlObjectName Table,
    string ArtifactName,
    string Detail);

internal sealed record SqlSchemaComparisonResult(
    ImmutableArray<SqlSchemaDifference> Differences)
{
    public bool IsExactMatch => Differences.IsEmpty;
}

internal static class SqlSchemaComparator
{
    public static SqlSchemaComparisonResult Compare(
        SqlSchemaDescriptor expected,
        SqlSchemaDescriptor actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        var differences = ImmutableArray.CreateBuilder<SqlSchemaDifference>();
        var expectedTables = expected.Tables.ToDictionary(static table => table.Name);
        var actualTables = actual.Tables.ToDictionary(static table => table.Name);

        foreach (var tableName in expectedTables.Keys.Union(actualTables.Keys)
                     .OrderBy(static name => name.SchemaName, StringComparer.Ordinal)
                     .ThenBy(static name => name.ObjectName, StringComparer.Ordinal))
        {
            var hasExpected = expectedTables.TryGetValue(tableName, out var expectedTable);
            var hasActual = actualTables.TryGetValue(tableName, out var actualTable);
            if (!hasActual)
            {
                differences.Add(Difference(SqlSchemaDifferenceKind.MissingOwnedTable, tableName, tableName.ObjectName, "Owned table is missing."));
                continue;
            }

            if (!hasExpected)
            {
                differences.Add(Difference(SqlSchemaDifferenceKind.UnexpectedOwnedTable, tableName, tableName.ObjectName, "Unexpected owned table is present."));
                continue;
            }

            CompareColumns(expectedTable!, actualTable!, differences);
            ComparePrimaryKey(expectedTable!, actualTable!, differences);
            CompareNamedArtifacts(
                expectedTable!.UniqueConstraints,
                actualTable!.UniqueConstraints,
                static item => item.Name,
                SqlSchemaDifferenceKind.UniqueConstraintMismatch,
                expectedTable.Name,
                static (left, right) => UniqueConstraintEquals(left, right),
                differences);
            CompareNamedArtifacts(
                expectedTable.ForeignKeys,
                actualTable.ForeignKeys,
                static item => item.Name,
                SqlSchemaDifferenceKind.ForeignKeyMismatch,
                expectedTable.Name,
                static (left, right) => ForeignKeyEquals(left, right),
                differences);
            CompareNamedArtifacts(
                expectedTable.CheckConstraints,
                actualTable.CheckConstraints,
                static item => item.Name,
                SqlSchemaDifferenceKind.CheckConstraintMismatch,
                expectedTable.Name,
                static (left, right) => CheckConstraintEquals(left, right),
                differences);
            CompareIndexes(expectedTable, actualTable, differences);
        }

        return new SqlSchemaComparisonResult(differences
            .OrderBy(static item => item.Table.SchemaName, StringComparer.Ordinal)
            .ThenBy(static item => item.Table.ObjectName, StringComparer.Ordinal)
            .ThenBy(static item => item.Kind)
            .ThenBy(static item => item.ArtifactName, StringComparer.Ordinal)
            .ThenBy(static item => item.Detail, StringComparer.Ordinal)
            .ToImmutableArray());
    }

    private static void CompareColumns(
        SqlTableDescriptor expected,
        SqlTableDescriptor actual,
        ImmutableArray<SqlSchemaDifference>.Builder differences)
    {
        var expectedColumns = expected.Columns.ToDictionary(static item => item.Name, StringComparer.Ordinal);
        var actualColumns = actual.Columns.ToDictionary(static item => item.Name, StringComparer.Ordinal);

        foreach (var name in expectedColumns.Keys.Union(actualColumns.Keys, StringComparer.Ordinal).OrderBy(static item => item, StringComparer.Ordinal))
        {
            var hasExpected = expectedColumns.TryGetValue(name, out var expectedColumn);
            var hasActual = actualColumns.TryGetValue(name, out var actualColumn);
            if (!hasActual)
            {
                differences.Add(Difference(SqlSchemaDifferenceKind.MissingColumn, expected.Name, name, "Column is missing."));
                continue;
            }

            if (!hasExpected)
            {
                differences.Add(Difference(SqlSchemaDifferenceKind.UnexpectedColumn, expected.Name, name, "Unexpected column is present."));
                continue;
            }

            AddMismatch(expected.Name, name, SqlSchemaDifferenceKind.ColumnTypeMismatch, expectedColumn!.SqlType, actualColumn!.SqlType, differences);
            AddMismatch(expected.Name, name, SqlSchemaDifferenceKind.ColumnLengthMismatch, expectedColumn.MaxLength, actualColumn.MaxLength, differences);
            AddMismatch(expected.Name, name, SqlSchemaDifferenceKind.ColumnPrecisionMismatch, expectedColumn.Precision, actualColumn.Precision, differences);
            AddMismatch(expected.Name, name, SqlSchemaDifferenceKind.ColumnScaleMismatch, expectedColumn.Scale, actualColumn.Scale, differences);
            AddMismatch(expected.Name, name, SqlSchemaDifferenceKind.ColumnNullabilityMismatch, expectedColumn.IsNullable, actualColumn.IsNullable, differences);
            AddMismatch(expected.Name, name, SqlSchemaDifferenceKind.ColumnCollationMismatch, expectedColumn.Collation, actualColumn.Collation, differences);
            if (!Equals(expectedColumn.Identity, actualColumn.Identity))
            {
                differences.Add(Difference(SqlSchemaDifferenceKind.ColumnIdentityMismatch, expected.Name, name, "Identity semantics differ."));
            }
        }
    }

    private static void ComparePrimaryKey(
        SqlTableDescriptor expected,
        SqlTableDescriptor actual,
        ImmutableArray<SqlSchemaDifference>.Builder differences)
    {
        if (PrimaryKeyEquals(expected.PrimaryKey, actual.PrimaryKey))
        {
            return;
        }

        var artifactName = expected.PrimaryKey?.Name ?? actual.PrimaryKey?.Name ?? "<primary-key>";
        differences.Add(Difference(SqlSchemaDifferenceKind.PrimaryKeyMismatch, expected.Name, artifactName, "Primary key semantics differ."));
    }

    private static void CompareIndexes(
        SqlTableDescriptor expected,
        SqlTableDescriptor actual,
        ImmutableArray<SqlSchemaDifference>.Builder differences)
    {
        var expectedIndexes = expected.Indexes.ToDictionary(static item => item.Name, StringComparer.Ordinal);
        var actualIndexes = actual.Indexes.ToDictionary(static item => item.Name, StringComparer.Ordinal);

        foreach (var name in expectedIndexes.Keys.Union(actualIndexes.Keys, StringComparer.Ordinal).OrderBy(static item => item, StringComparer.Ordinal))
        {
            var hasExpected = expectedIndexes.TryGetValue(name, out var expectedIndex);
            var hasActual = actualIndexes.TryGetValue(name, out var actualIndex);
            if (!hasActual)
            {
                differences.Add(Difference(SqlSchemaDifferenceKind.MissingIndex, expected.Name, name, "Required index is missing."));
            }
            else if (!hasExpected)
            {
                differences.Add(Difference(SqlSchemaDifferenceKind.UnexpectedIndex, expected.Name, name, "Unexpected index is present."));
            }
            else if (!IndexEquals(expectedIndex!, actualIndex!))
            {
                differences.Add(Difference(SqlSchemaDifferenceKind.IndexMismatch, expected.Name, name, "Index semantics differ."));
            }
        }
    }

    private static void CompareNamedArtifacts<T>(
        ImmutableArray<T> expected,
        ImmutableArray<T> actual,
        Func<T, string> getName,
        SqlSchemaDifferenceKind mismatchKind,
        SqlObjectName table,
        Func<T, T, bool> equals,
        ImmutableArray<SqlSchemaDifference>.Builder differences)
    {
        var expectedByName = expected.ToDictionary(getName, StringComparer.Ordinal);
        var actualByName = actual.ToDictionary(getName, StringComparer.Ordinal);

        foreach (var name in expectedByName.Keys.Union(actualByName.Keys, StringComparer.Ordinal).OrderBy(static item => item, StringComparer.Ordinal))
        {
            if (!expectedByName.TryGetValue(name, out var expectedItem) ||
                !actualByName.TryGetValue(name, out var actualItem) ||
                !equals(expectedItem, actualItem))
            {
                differences.Add(Difference(mismatchKind, table, name, "Structural or operational semantics differ."));
            }
        }
    }

    private static bool PrimaryKeyEquals(SqlPrimaryKeyDescriptor? left, SqlPrimaryKeyDescriptor? right) =>
        left is null && right is null ||
        left is not null && right is not null &&
        string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
        left.IsEnabled == right.IsEnabled &&
        IndexStructureEquals(left.IndexStructure, right.IndexStructure);

    private static bool UniqueConstraintEquals(SqlUniqueConstraintDescriptor left, SqlUniqueConstraintDescriptor right) =>
        string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
        left.IsEnabled == right.IsEnabled &&
        IndexStructureEquals(left.IndexStructure, right.IndexStructure);

    private static bool ForeignKeyEquals(SqlForeignKeyDescriptor left, SqlForeignKeyDescriptor right) =>
        string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
        left.Columns.SequenceEqual(right.Columns, StringComparer.Ordinal) &&
        left.ReferencedTable == right.ReferencedTable &&
        left.ReferencedColumns.SequenceEqual(right.ReferencedColumns, StringComparer.Ordinal) &&
        left.DeleteAction == right.DeleteAction &&
        left.UpdateAction == right.UpdateAction &&
        left.IsEnabled == right.IsEnabled &&
        left.IsTrusted == right.IsTrusted &&
        left.IsNotForReplication == right.IsNotForReplication;

    private static bool CheckConstraintEquals(SqlCheckConstraintDescriptor left, SqlCheckConstraintDescriptor right) =>
        string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
        string.Equals(
            SqlFragmentCanonicalizer.Canonicalize(left.CanonicalDefinition),
            SqlFragmentCanonicalizer.Canonicalize(right.CanonicalDefinition),
            StringComparison.Ordinal) &&
        left.IsEnabled == right.IsEnabled &&
        left.IsTrusted == right.IsTrusted &&
        left.IsNotForReplication == right.IsNotForReplication;

    private static bool IndexEquals(SqlIndexDescriptor left, SqlIndexDescriptor right) =>
        string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
        left.IsUnique == right.IsUnique &&
        left.IsEnabled == right.IsEnabled &&
        IndexStructureEquals(left.IndexStructure, right.IndexStructure);

    private static bool IndexStructureEquals(SqlIndexStructureDescriptor left, SqlIndexStructureDescriptor right) =>
        left.IsClustered == right.IsClustered &&
        left.KeyColumns.SequenceEqual(right.KeyColumns) &&
        left.IncludedColumns.SequenceEqual(right.IncludedColumns, StringComparer.Ordinal) &&
        CanonicalNullableFragmentEquals(left.CanonicalFilterDefinition, right.CanonicalFilterDefinition);

    private static bool CanonicalNullableFragmentEquals(string? left, string? right) =>
        left is null && right is null ||
        left is not null && right is not null &&
        string.Equals(
            SqlFragmentCanonicalizer.Canonicalize(left),
            SqlFragmentCanonicalizer.Canonicalize(right),
            StringComparison.Ordinal);

    private static void AddMismatch<T>(
        SqlObjectName table,
        string artifactName,
        SqlSchemaDifferenceKind kind,
        T expected,
        T actual,
        ImmutableArray<SqlSchemaDifference>.Builder differences)
    {
        if (EqualityComparer<T>.Default.Equals(expected, actual))
        {
            return;
        }

        differences.Add(Difference(kind, table, artifactName, $"Expected '{expected}'; actual '{actual}'."));
    }

    private static SqlSchemaDifference Difference(
        SqlSchemaDifferenceKind kind,
        SqlObjectName table,
        string artifactName,
        string detail) => new(kind, table, artifactName, detail);
}
