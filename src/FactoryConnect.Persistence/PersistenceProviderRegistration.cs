namespace FactoryConnect.Persistence;

public sealed class PersistenceProviderRegistration :
    IPersistenceProviderRegistration
{
    private readonly Func<
        IServiceProvider,
        PersistenceProviderServices> _factory;

    public string ProviderKey { get; }

    public PersistenceProviderRegistration(
        string providerKey,
        Func<IServiceProvider, PersistenceProviderServices> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        ProviderKey = PersistenceProviderKey.Normalize(providerKey);
        _factory = factory;
    }

    public PersistenceProviderServices Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return _factory(services)
            ?? throw new InvalidOperationException(
                $"Persistence provider '{ProviderKey}' returned no services.");
    }
}
