namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlServerSchema
{
    private const string InitialSchemaResourceName =
        "FactoryConnect.Persistence.SqlServer.Sql.001_InitialObservationIngestion.sql";
    private const string MetricAggregationSchemaResourceName =
        "FactoryConnect.Persistence.SqlServer.Sql.002_DurableMetricAggregation.sql";
    private const string MetricInputMachineBindingSchemaResourceName =
        "FactoryConnect.Persistence.SqlServer.Sql.003_BindMetricInputFactMachine.sql";
    private const string ProductionContextHandoffSchemaResourceName =
        "FactoryConnect.Persistence.SqlServer.Sql.004_ProductionContextMetricInputHandoff.sql";
    private const string OperationalMetricReportingPersistenceSchemaResourceName =
        "FactoryConnect.Persistence.SqlServer.Sql.005_OperationalMetricReportingPersistence.sql";
    private const string CorrectOperationalMetricProjectionManifestParentSchemaResourceName =
        "FactoryConnect.Persistence.SqlServer.Sql.006_CorrectOperationalMetricProjectionManifestParent.sql";

    public static string ReadInitialSchema() =>
        ReadSchema(InitialSchemaResourceName);

    public static string ReadMetricAggregationSchema() =>
        ReadSchema(MetricAggregationSchemaResourceName);

    public static string ReadMetricInputMachineBindingSchema() =>
        ReadSchema(MetricInputMachineBindingSchemaResourceName);

    public static string ReadProductionContextHandoffSchema() =>
        ReadSchema(ProductionContextHandoffSchemaResourceName);

    public static string ReadOperationalMetricReportingPersistenceSchema() =>
        ReadSchema(OperationalMetricReportingPersistenceSchemaResourceName);

    public static string ReadCorrectOperationalMetricProjectionManifestParentSchema() =>
        ReadSchema(CorrectOperationalMetricProjectionManifestParentSchemaResourceName);

    private static string ReadSchema(string resourceName)
    {
        var assembly = typeof(SqlServerSchema).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded SQL schema '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
