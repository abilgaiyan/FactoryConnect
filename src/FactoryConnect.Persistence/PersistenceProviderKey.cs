namespace FactoryConnect.Persistence;

public static class PersistenceProviderKey
{
    public static string Normalize(string providerKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);

        return providerKey.Trim().ToUpperInvariant();
    }
}
