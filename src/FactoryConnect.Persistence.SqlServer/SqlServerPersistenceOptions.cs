namespace FactoryConnect.Persistence.SqlServer;

public sealed class SqlServerPersistenceOptions
{
    public const string SectionName = "PersistenceProviders:SqlServer";

    public string? ConnectionString { get; set; }

    internal string GetRequiredConnectionString()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new InvalidOperationException(
                $"{SectionName}:ConnectionString is required.");
        }

        return ConnectionString;
    }
}
