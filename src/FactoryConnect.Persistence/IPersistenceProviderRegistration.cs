namespace FactoryConnect.Persistence;

public interface IPersistenceProviderRegistration
{
    string ProviderKey { get; }

    PersistenceProviderServices Create(IServiceProvider services);
}
