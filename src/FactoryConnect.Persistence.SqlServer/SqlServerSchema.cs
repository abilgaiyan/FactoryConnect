namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlServerSchema
{
    private const string InitialSchemaResourceName =
        "FactoryConnect.Persistence.SqlServer.Sql.001_InitialObservationIngestion.sql";

    public static string ReadInitialSchema()
    {
        var assembly = typeof(SqlServerSchema).Assembly;
        using var stream = assembly.GetManifestResourceStream(
            InitialSchemaResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded SQL schema '{InitialSchemaResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
