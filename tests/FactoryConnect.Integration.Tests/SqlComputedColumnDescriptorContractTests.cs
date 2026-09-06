using System.Collections.Immutable;
using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlComputedColumnDescriptorContractTests
{
    [Fact]
    public void ColumnDescriptorRetainsComputedDefinitionAndPersistence()
    {
        var computed = new SqlComputedColumnDescriptor("CONVERT(binary(16), [MachineId])", IsPersisted: true);
        var column = Column(computed);

        Assert.Same(computed, column.Computed);
        Assert.Equal("CONVERT(binary(16), [MachineId])", column.Computed!.CanonicalDefinition);
        Assert.True(column.Computed.IsPersisted);
    }

    [Fact]
    public void ComparatorRejectsOrdinaryColumnSubstitution()
    {
        AssertComputedMismatch(Column(new SqlComputedColumnDescriptor("([Value]+(1))", true)), Column(null));
    }

    [Fact]
    public void ComparatorRejectsUnexpectedComputedColumn()
    {
        AssertComputedMismatch(Column(null), Column(new SqlComputedColumnDescriptor("([Value]+(1))", true)));
    }

    [Fact]
    public void ComparatorRejectsChangedComputedExpression()
    {
        AssertComputedMismatch(
            Column(new SqlComputedColumnDescriptor("([Value]+(1))", true)),
            Column(new SqlComputedColumnDescriptor("([Value]+(2))", true)));
    }

    [Fact]
    public void ComparatorRejectsPersistedStateChange()
    {
        AssertComputedMismatch(
            Column(new SqlComputedColumnDescriptor("([Value]+(1))", true)),
            Column(new SqlComputedColumnDescriptor("([Value]+(1))", false)));
    }

    [Fact]
    public void ComparatorCanonicalizesEquivalentComputedDefinitions()
    {
        var expected = Schema(Column(new SqlComputedColumnDescriptor(" /* authority */ ( [Value] + (1) ) ", true)));
        var actual = Schema(Column(new SqlComputedColumnDescriptor("([Value]+(1))", true)));

        Assert.True(SqlSchemaComparator.Compare(expected, actual).IsExactMatch);
    }

    private static void AssertComputedMismatch(SqlColumnDescriptor expected, SqlColumnDescriptor actual)
    {
        var difference = Assert.Single(SqlSchemaComparator.Compare(Schema(expected), Schema(actual)).Differences);
        Assert.Equal(SqlSchemaDifferenceKind.ColumnComputedMismatch, difference.Kind);
        Assert.Equal("ComputedValue", difference.ArtifactName);
    }

    private static SqlSchemaDescriptor Schema(SqlColumnDescriptor column) => new(
        [new SqlTableDescriptor(new SqlObjectName("dbo", "ComputedAuthority"), [column], null, [], [], [], [])]);

    private static SqlColumnDescriptor Column(SqlComputedColumnDescriptor? computed) => new(
        "ComputedValue",
        "int",
        MaxLength: null,
        Precision: null,
        Scale: null,
        IsNullable: true,
        Collation: null,
        Identity: null,
        Computed: computed);
}
