namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlRepositorySchemaAuthority
{
    public static SqlOwnedObjectRecognitionSet OwnedObjects { get; } = new(
    [
        Table("ContextualizedActivityOutput"),
        Table("MachineObservation"),
        Table("MetricAggregationCheckpoint"),
        Table("MetricAggregationContribution"),
        Table("MetricAggregationProcessor"),
        Table("MetricInputFact"),
        Table("MetricInputStream"),
        Table("ObservationStreamCheckpoint"),
        Table("ProductionContextCheckpoint"),
        Table("ProductionContextProcessor"),
        Table("ProductionDayMetricAggregate"),
        Table("ProductionTimeEligibilityOutput"),
        Table("ShiftMetricAggregate")
    ]);

    private static SqlObjectName Table(string tableName) => new("dbo", tableName);
}
