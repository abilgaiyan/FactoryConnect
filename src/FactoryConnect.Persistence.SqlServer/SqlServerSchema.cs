namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlServerSchema
{
    private const string InitialSchemaResourceName =
        "FactoryConnect.Persistence.SqlServer.Sql.001_InitialObservationIngestion.sql";
    private const string MetricAggregationSchemaResourceName =
        "FactoryConnect.Persistence.SqlServer.Sql.002_DurableMetricAggregation.sql";

    public static string ReadInitialSchema() =>
        ReadSchema(InitialSchemaResourceName);

    public static string ReadMetricAggregationSchema() =>
        ReadSchema(MetricAggregationSchemaResourceName);

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
