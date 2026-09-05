namespace FactoryConnect.Persistence.SqlServer;

public sealed class SqlServerPersistenceOptions
{
    public const string SectionName = "PersistenceProviders:SqlServer";

    public string? ConnectionString { get; set; }

    public SqlServerStartupOptions Startup { get; set; } = new();

    internal string GetRequiredConnectionString()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new InvalidOperationException(
                $"{SectionName}:ConnectionString is required.");
        }

        return ConnectionString;
    }

    internal SqlPersistenceStartupOptions GetStartupOptions()
    {
        Startup ??= new SqlServerStartupOptions();
        return new SqlPersistenceStartupOptions(Startup.LockTimeout);
    }
}

public sealed class SqlServerStartupOptions
{
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
