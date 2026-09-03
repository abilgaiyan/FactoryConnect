using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlRepositorySchemaAuthorityTests
{
    private static readonly string[] ExpectedOwnedTableNames =
    [
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
    ];

    [Fact]
    public void OwnedObjectsFreezeExactPost004TableRecognitionSet()
    {
        var ownedTables = SqlRepositorySchemaAuthority.OwnedObjects.OwnedTables;

        Assert.Equal(13, ownedTables.Length);
        Assert.All(ownedTables, static table => Assert.Equal("dbo", table.SchemaName));
        Assert.Equal(ExpectedOwnedTableNames, ownedTables.Select(static table => table.ObjectName));
    }

    [Fact]
    public void OwnedObjectsDoNotTreatDboSchemaAsOwned()
    {
        var ownedObjects = SqlRepositorySchemaAuthority.OwnedObjects;

        Assert.False(ownedObjects.Recognizes(new SqlObjectName("dbo", "CustomerOrders")));
        Assert.False(ownedObjects.Recognizes(new SqlObjectName("audit", "MetricInputFact")));
    }
}
