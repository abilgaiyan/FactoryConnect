using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlRepositorySchemaDescriptorTests
{
    [Fact]
    public void LegacyPost004AndCurrentAreDistinctRepositoryValuesWithSameTableIdentities()
    {
        var legacy = SqlRepositorySchemaDescriptors.LegacyPost004;
        var current = SqlRepositorySchemaDescriptors.Current;

        Assert.NotSame(legacy, current);
        Assert.Equal(
            legacy.Tables.Select(static table => table.Name),
            current.Tables.Select(static table => table.Name));
    }

    [Fact]
    public void LegacyPost004ContainsExactlyTheRecognizedPost004Tables()
    {
        var expected = SqlRepositorySchemaAuthority.OwnedObjects.OwnedTables
            .OrderBy(static table => table.SchemaName, StringComparer.Ordinal)
            .ThenBy(static table => table.ObjectName, StringComparer.Ordinal)
            .ToArray();
        var actual = SqlRepositorySchemaDescriptors.LegacyPost004.Tables
            .Select(static table => table.Name)
            .OrderBy(static table => table.SchemaName, StringComparer.Ordinal)
            .ThenBy(static table => table.ObjectName, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void MetricInputFactReflectsFinalMigration003Relationship()
    {
        var table = FindLegacyTable("MetricInputFact");

        Assert.DoesNotContain(
            table.ForeignKeys,
            static foreignKey => string.Equals(
                foreignKey.Name,
                "FK_MetricInputFact_MetricInputStream",
                StringComparison.Ordinal));

        var foreignKey = Assert.Single(
            table.ForeignKeys,
            static foreignKey => string.Equals(
                foreignKey.Name,
                "FK_MetricInputFact_StreamMachine",
                StringComparison.Ordinal));

        Assert.Equal(["MetricInputStreamRowId", "MachineId"], foreignKey.Columns);
        Assert.Equal(["MetricInputStreamRowId", "MachineId"], foreignKey.ReferencedColumns);
        Assert.True(foreignKey.IsEnabled);
        Assert.True(foreignKey.IsTrusted);
        Assert.Equal(SqlReferentialAction.NoAction, foreignKey.DeleteAction);
        Assert.Equal(SqlReferentialAction.NoAction, foreignKey.UpdateAction);
    }

    [Fact]
    public void MetricInputStreamReflectsMigration003CompositeUniqueConstraint()
    {
        var table = FindLegacyTable("MetricInputStream");
        var constraint = Assert.Single(
            table.UniqueConstraints,
            static constraint => string.Equals(
                constraint.Name,
                "UQ_MetricInputStream_RowMachine",
                StringComparison.Ordinal));

        Assert.False(constraint.IndexStructure.IsClustered);
        Assert.Equal(
            ["MetricInputStreamRowId", "MachineId"],
            constraint.IndexStructure.KeyColumns.Select(static column => column.Name));
    }

    [Fact]
    public void LegacyDescriptorRetainsIdentityAndCoveringIndexSemantics()
    {
        var metricInput = FindLegacyTable("MetricInputFact");
        var identity = Assert.IsType<SqlIdentityDescriptor>(metricInput.Columns[0].Identity);
        Assert.Equal(1m, identity.SeedValue);
        Assert.Equal(1m, identity.IncrementValue);
        Assert.False(identity.IsNotForReplication);

        var index = Assert.Single(
            metricInput.Indexes,
            static index => string.Equals(
                index.Name,
                "IX_MetricInputFact_OrderedRead",
                StringComparison.Ordinal));
        Assert.False(index.IsUnique);
        Assert.False(index.IndexStructure.IsClustered);
        Assert.True(index.IsEnabled);
        Assert.Equal(
            ["MetricInputStreamRowId", "Position"],
            index.IndexStructure.KeyColumns.Select(static column => column.Name));
        Assert.All(
            index.IndexStructure.KeyColumns,
            static column => Assert.Equal(SqlIndexColumnDirection.Ascending, column.Direction));
        Assert.Equal(
            ["MetricInputFactRowId", "FactId", "MetricInputKey", "MetricValue", "Unit"],
            index.IndexStructure.IncludedColumns);
    }

    [Fact]
    public void OutputTablesAreBothPresentInPost004Descriptor()
    {
        Assert.Equal(
            "ContextualizedActivityOutputRowId",
            FindLegacyTable("ContextualizedActivityOutput").Columns[0].Name);
        Assert.Equal(
            "ProductionTimeEligibilityOutputRowId",
            FindLegacyTable("ProductionTimeEligibilityOutput").Columns[0].Name);
    }

    private static SqlTableDescriptor FindLegacyTable(string tableName) =>
        Assert.Single(
            SqlRepositorySchemaDescriptors.LegacyPost004.Tables,
            table => string.Equals(table.Name.ObjectName, tableName, StringComparison.Ordinal));
}
