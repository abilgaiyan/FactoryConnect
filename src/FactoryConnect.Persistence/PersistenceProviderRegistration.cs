namespace FactoryConnect.Persistence;

public sealed class PersistenceProviderRegistration :
    IPersistenceProviderRegistration
{
    private readonly Func<
        IServiceProvider,
        PersistenceProviderServices> _factory;

    public PersistenceProviderRegistration(
        string providerKey,
        PersistenceProviderCapabilities capabilities,
        Func<IServiceProvider, PersistenceProviderServices> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        ProviderKey = PersistenceProviderKey.Normalize(providerKey);
        Capabilities = capabilities;
        _factory = factory;
    }

    public string ProviderKey { get; }

    public PersistenceProviderCapabilities Capabilities { get; }

    public PersistenceProviderServices Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return _factory(services)
            ?? throw new InvalidOperationException(
                $"Persistence provider '{ProviderKey}' returned no services.");
    }
}
