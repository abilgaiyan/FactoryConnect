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
        Assert.Contains("CK_MetricInputFact_SiteOwnership", schema, StringComparison.Ordinal);
        Assert.Contains("CK_MetricInputFact_ShiftOwnership", schema, StringComparison.Ordinal);
        Assert.Contains("CK_MetricInputFact_ScheduleOwnership", schema, StringComparison.Ordinal);
        Assert.Contains("CK_MetricInputFact_UtcOffsets", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void AggregationSchemaStructurallyBindsContributionToProcessorStreamFactAndPosition()
    {
        var schema = SqlServerSchema.ReadMetricAggregationSchema();

        Assert.Contains("UQ_MetricAggregationProcessor_StreamBinding", schema, StringComparison.Ordinal);
        Assert.Contains("UQ_MetricInputFact_StreamPositionRow", schema, StringComparison.Ordinal);
        Assert.Contains("FK_MetricAggregationContribution_ProcessorStream", schema, StringComparison.Ordinal);
        Assert.Contains("FK_MetricAggregationContribution_FactStreamPosition", schema, StringComparison.Ordinal);
        Assert.Contains("PK_MetricAggregationCheckpoint", schema, StringComparison.Ordinal);
        Assert.Contains("CK_MetricAggregationCheckpoint_Position_UInt64", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void AggregateSchemaUsesSurrogateClusteredKeysAndFixedSizeIdentityHashes()
    {
        var schema = SqlServerSchema.ReadMetricAggregationSchema();

        Assert.Contains("ShiftMetricAggregateRowId bigint IDENTITY(1,1)", schema, StringComparison.Ordinal);
        Assert.Contains("ProductionDayMetricAggregateRowId bigint IDENTITY(1,1)", schema, StringComparison.Ordinal);
        Assert.Contains("AggregateKeyHash binary(32) NOT NULL", schema, StringComparison.Ordinal);
        Assert.Contains("AggregateKeyBinary varbinary(max) NOT NULL", schema, StringComparison.Ordinal);
        Assert.Contains("UQ_ShiftMetricAggregate_IdentityHash", schema, StringComparison.Ordinal);
        Assert.Contains("UQ_ProductionDayMetricAggregate_IdentityHash", schema, StringComparison.Ordinal);
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
