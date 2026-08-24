using FactoryConnect.Abstractions;

namespace FactoryConnect.Persistence;

public interface IPersistenceProviderRegistration
{
    string ProviderKey { get; }

    IObservationIngestionStore Create(IServiceProvider services);
}
