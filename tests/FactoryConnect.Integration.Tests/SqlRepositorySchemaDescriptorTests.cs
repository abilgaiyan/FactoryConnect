using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlRepositorySchemaDescriptorTests
{
    [Fact]
    public void CurrentExtendsLegacyPost004ThroughPost007()
    {
        var legacy = SqlRepositorySchemaDescriptors.LegacyPost004;
        var post005 = SqlRepositorySchemaDescriptors.Post005;
        var current = SqlRepositorySchemaDescriptors.Current;

        Assert.NotSame(legacy, current);
        Assert.Equal(13, legacy.Tables.Length);
        Assert.Equal(20, post005.Tables.Length);
        Assert.Equal(21, current.Tables.Length);
        Assert.Equal(
            legacy.Tables.Select(static table => table.Name),
            current.Tables.Take(legacy.Tables.Length).Select(static table => table.Name));
        Assert.Equal(
            [
                "OperationalMetricProjectionProcessor",
                "OperationalMetricProjectionCheckpoint",
                "OperationalMetricProjection",
                "OperationalMetricProjectionManifest",
                "OperationalMetricProjectionEvidence",
                "MachineShiftOccurrenceRoster",
                "MachineShiftOccurrenceRosterOccurrence",
                "AcquisitionContactAuthority"
            ],
            current.Tables.Skip(legacy.Tables.Length).Select(static table => table.Name.ObjectName));
    }

    [Fact]
    public void LegacyPost004ContainsExactlyTheFrozenPost004Tables()
    {
        var expected = new[]
        {
            "ContextualizedActivityOutput",
            "MachineObservation",
            "MetricAggregationCheckpoint",
            "MetricAggregationContribution",
            "MetricAggregationProcessor",
            "MetricInputFact",
            "MetricInputStream",
            "ObservationStreamCheckpoint",
            "ProductionContextCheckpoint",
            "ProductionContextProcessor",
            "ProductionDayMetricAggregate",
            "ProductionTimeEligibilityOutput",
            "ShiftMetricAggregate"
        };
        var actual = SqlRepositorySchemaDescriptors.LegacyPost004.Tables
            .Select(static table => table.Name.ObjectName)
            .OrderBy(static table => table, StringComparer.Ordinal)
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

        Assert.Equal<string>(["MetricInputStreamRowId", "MachineId"], foreignKey.Columns);
        Assert.Equal<string>(["MetricInputStreamRowId", "MachineId"], foreignKey.ReferencedColumns);
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
        Assert.Equal<string>(
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
