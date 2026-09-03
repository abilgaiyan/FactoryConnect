using System.Collections.Immutable;
using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlSchemaComparatorTests
{
    [Fact]
    public void ExactMatchIgnoresUnorderedCollectionOrdering()
    {
        var expected = Schema(
            Table(
                "B",
                columns: [Column("Second", "int"), Column("First", "int")],
                checks:
                [
                    Check("CK_B_Z", "([Second] > 0)"),
                    Check("CK_B_A", "([First] > 0)")
                ]),
            Table("A", columns: [Column("Id", "bigint")]));
        var actual = Schema(
            Table("A", columns: [Column("Id", "bigint")]),
            Table(
                "B",
                columns: [Column("First", "int"), Column("Second", "int")],
                checks:
                [
                    Check("CK_B_A", " /* comment */ ( [First] > 0 ) "),
                    Check("CK_B_Z", "([Second]>0)")
                ]));

        var result = SqlSchemaComparator.Compare(expected, actual);

        Assert.True(result.IsExactMatch);
        Assert.Empty(result.Differences);
    }

    [Fact]
    public void ColumnPhysicalOrderingDoesNotParticipate()
    {
        var expected = Schema(Table("T", columns: [Column("A", "int"), Column("B", "bigint")]));
        var actual = Schema(Table("T", columns: [Column("B", "bigint"), Column("A", "int")]));

        Assert.True(SqlSchemaComparator.Compare(expected, actual).IsExactMatch);
    }

    [Fact]
    public void KeyOrderingRemainsSemantic()
    {
        var expected = Schema(Table("T", columns: [Column("A", "int"), Column("B", "int")], primaryKey: PrimaryKey("PK_T", "A", "B")));
        var actual = Schema(Table("T", columns: [Column("A", "int"), Column("B", "int")], primaryKey: PrimaryKey("PK_T", "B", "A")));

        var difference = Assert.Single(SqlSchemaComparator.Compare(expected, actual).Differences);

        Assert.Equal(SqlSchemaDifferenceKind.PrimaryKeyMismatch, difference.Kind);
        Assert.Equal("PK_T", difference.ArtifactName);
    }

    [Fact]
    public void PrimaryKeyEnabledStateParticipates()
    {
        var structure = IndexStructure("Id");
        var expected = Schema(Table("T", columns: [Column("Id", "bigint")], primaryKey: new SqlPrimaryKeyDescriptor("PK_T", IsEnabled: true, structure)));
        var actual = Schema(Table("T", columns: [Column("Id", "bigint")], primaryKey: new SqlPrimaryKeyDescriptor("PK_T", IsEnabled: false, structure)));

        Assert.Equal(
            SqlSchemaDifferenceKind.PrimaryKeyMismatch,
            Assert.Single(SqlSchemaComparator.Compare(expected, actual).Differences).Kind);
    }

    [Fact]
    public void CheckDefinitionsUseLexicalCanonicalization()
    {
        var expected = Schema(Table("T", columns: [Column("A", "int")], checks: [Check("CK_T_A", "([A] > 0)")]));
        var equivalent = Schema(Table("T", columns: [Column("A", "int")], checks: [Check("CK_T_A", " /*x*/ ( [A]>0 )")]));
        var reordered = Schema(Table("T", columns: [Column("A", "int")], checks: [Check("CK_T_A", "(0 < [A])")]));

        Assert.True(SqlSchemaComparator.Compare(expected, equivalent).IsExactMatch);
        Assert.False(SqlSchemaComparator.Compare(expected, reordered).IsExactMatch);
    }

    [Fact]
    public void FilterDefinitionsUseLexicalCanonicalization()
    {
        var expected = Schema(Table("T", columns: [Column("A", "int")], indexes: [Index("IX_T_A", "([A] > 0)")]));
        var actual = Schema(Table("T", columns: [Column("A", "int")], indexes: [Index("IX_T_A", "/*x*/ ( [A]>0 )")]));

        Assert.True(SqlSchemaComparator.Compare(expected, actual).IsExactMatch);
    }

    [Fact]
    public void DiagnosticsAreStableAcrossInputOrdering()
    {
        var expected = Schema(
            Table("Z", columns: [Column("B", "int"), Column("A", "int")]),
            Table("A", columns: [Column("Id", "int")]));
        var actual = Schema(
            Table("Z", columns: [Column("C", "int"), Column("B", "bigint")]),
            Table("A", columns: [Column("Id", "bigint")]));
        var reversedExpected = new SqlSchemaDescriptor(expected.Tables.Reverse().ToImmutableArray());
        var reversedActual = new SqlSchemaDescriptor(actual.Tables.Reverse().ToImmutableArray());

        var first = SqlSchemaComparator.Compare(expected, actual).Differences;
        var second = SqlSchemaComparator.Compare(reversedExpected, reversedActual).Differences;

        Assert.Equal(first, second);
        Assert.Collection(
            first,
            difference =>
            {
                Assert.Equal(new SqlObjectName("dbo", "A"), difference.Table);
                Assert.Equal(SqlSchemaDifferenceKind.ColumnTypeMismatch, difference.Kind);
                Assert.Equal("Id", difference.ArtifactName);
            },
            difference =>
            {
                Assert.Equal(new SqlObjectName("dbo", "Z"), difference.Table);
                Assert.Equal(SqlSchemaDifferenceKind.MissingColumn, difference.Kind);
                Assert.Equal("A", difference.ArtifactName);
            },
            difference =>
            {
                Assert.Equal(new SqlObjectName("dbo", "Z"), difference.Table);
                Assert.Equal(SqlSchemaDifferenceKind.UnexpectedColumn, difference.Kind);
                Assert.Equal("C", difference.ArtifactName);
            },
            difference =>
            {
                Assert.Equal(new SqlObjectName("dbo", "Z"), difference.Table);
                Assert.Equal(SqlSchemaDifferenceKind.ColumnTypeMismatch, difference.Kind);
                Assert.Equal("B", difference.ArtifactName);
            });
    }

    private static SqlSchemaDescriptor Schema(params SqlTableDescriptor[] tables) => new(tables.ToImmutableArray());

    private static SqlTableDescriptor Table(
        string name,
        ImmutableArray<SqlColumnDescriptor> columns,
        SqlPrimaryKeyDescriptor? primaryKey = null,
        ImmutableArray<SqlCheckConstraintDescriptor> checks = default,
        ImmutableArray<SqlIndexDescriptor> indexes = default) => new(
            new SqlObjectName("dbo", name),
            columns,
            primaryKey,
            UniqueConstraints: [],
            ForeignKeys: [],
            checks.IsDefault ? [] : checks,
            indexes.IsDefault ? [] : indexes);

    private static SqlColumnDescriptor Column(string name, string sqlType) => new(
        name,
        sqlType,
        MaxLength: null,
        Precision: null,
        Scale: null,
        IsNullable: false,
        Collation: null,
        Identity: null);

    private static SqlCheckConstraintDescriptor Check(string name, string definition) => new(
        name,
        definition,
        IsEnabled: true,
        IsTrusted: true,
        IsNotForReplication: false);

    private static SqlPrimaryKeyDescriptor PrimaryKey(string name, params string[] columns) => new(
        name,
        IsEnabled: true,
        IndexStructure(columns));

    private static SqlIndexDescriptor Index(string name, string filter) => new(
        name,
        IsUnique: false,
        IsEnabled: true,
        new SqlIndexStructureDescriptor(
            IsClustered: false,
            KeyColumns: [new SqlIndexColumnDescriptor("A", SqlIndexColumnDirection.Ascending, 1)],
            IncludedColumns: [],
            CanonicalFilterDefinition: filter));

    private static SqlIndexStructureDescriptor IndexStructure(params string[] columns) => new(
        IsClustered: true,
        KeyColumns: columns.Select(static (column, index) => new SqlIndexColumnDescriptor(column, SqlIndexColumnDirection.Ascending, index + 1)).ToImmutableArray(),
        IncludedColumns: [],
        CanonicalFilterDefinition: null);
}
