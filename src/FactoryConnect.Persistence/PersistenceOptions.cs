namespace FactoryConnect.Persistence;

public sealed record PersistenceOptions
{
    public string Provider { get; }

    public PersistenceOptions(string provider)
    {
        Provider = PersistenceProviderKey.Normalize(provider);
    }
}
