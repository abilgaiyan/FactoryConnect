using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlServerMetricAggregationSchemaTests
{
    private static readonly string[] RequiredTables =
    [
        "dbo.MetricInputStream",
        "dbo.MetricInputFact",
        "dbo.MetricAggregationProcessor",
        "dbo.MetricAggregationCheckpoint",
        "dbo.MetricAggregationContribution",
        "dbo.ShiftMetricAggregate",
        "dbo.ProductionDayMetricAggregate",
    ];

    [Fact]
    public void MetricAggregationSchemaContainsRequiredDurableTables()
    {
        var schema = SqlServerSchema.ReadMetricAggregationSchema();

        foreach (var table in RequiredTables)
        {
            Assert.Contains($"CREATE TABLE {table}", schema, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MetricInputSchemaPreservesOrderedStreamAndReplayIdentity()
    {
        var schema = SqlServerSchema.ReadMetricAggregationSchema();

        Assert.Contains("UQ_MetricInputStream_Identity", schema, StringComparison.Ordinal);
        Assert.Contains("UQ_MetricInputFact_StreamPosition", schema, StringComparison.Ordinal);
        Assert.Contains("UQ_MetricInputFact_StreamFactIdentity", schema, StringComparison.Ordinal);
        Assert.Contains("CK_MetricInputFact_Position_UInt64", schema, StringComparison.Ordinal);
        Assert.Contains("CK_MetricInputFact_OwnershipContainment", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void AggregationSchemaContainsContributionAndCheckpointConcurrencyBoundaries()
    {
        var schema = SqlServerSchema.ReadMetricAggregationSchema();

        Assert.Contains("PK_MetricAggregationContribution", schema, StringComparison.Ordinal);
        Assert.Contains("UQ_MetricAggregationContribution_Position", schema, StringComparison.Ordinal);
        Assert.Contains("PK_MetricAggregationCheckpoint", schema, StringComparison.Ordinal);
        Assert.Contains("CK_MetricAggregationCheckpoint_Position_UInt64", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void AggregateSchemaKeepsUnitOutsideAggregateIdentity()
    {
        var schema = SqlServerSchema.ReadMetricAggregationSchema();

        Assert.Contains("AggregateKeyBinary varbinary(900) NOT NULL", schema, StringComparison.Ordinal);
        Assert.Contains("Unit nvarchar(128)", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIMARY KEY (Unit", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void AggregateSchemaProvidesShiftAndProductionDayQueryIndexes()
    {
        var schema = SqlServerSchema.ReadMetricAggregationSchema();

        Assert.Contains("IX_ShiftMetricAggregate_Query", schema, StringComparison.Ordinal);
        Assert.Contains("IX_ProductionDayMetricAggregate_Query", schema, StringComparison.Ordinal);
    }
}
