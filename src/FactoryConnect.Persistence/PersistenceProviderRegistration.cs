using FactoryConnect.Abstractions;

namespace FactoryConnect.Persistence;

public sealed class PersistenceProviderRegistration :
    IPersistenceProviderRegistration
{
    private readonly Func<
        IServiceProvider,
        IObservationIngestionStore> _factory;

    public string ProviderKey { get; }

    public PersistenceProviderRegistration(
        string providerKey,
        Func<IServiceProvider, IObservationIngestionStore> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        ProviderKey = PersistenceProviderKey.Normalize(providerKey);
        _factory = factory;
    }

    public IObservationIngestionStore Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return _factory(services)
            ?? throw new InvalidOperationException(
                $"Persistence provider '{ProviderKey}' returned no store.");
    }
}
