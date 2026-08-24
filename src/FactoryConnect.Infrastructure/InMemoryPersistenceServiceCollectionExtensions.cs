using FactoryConnect.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.Infrastructure;

public static class InMemoryPersistenceServiceCollectionExtensions
{
    public const string ProviderKey = "InMemory";

    public static IServiceCollection AddInMemoryPersistenceProvider(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddPersistenceProvider(
            new PersistenceProviderRegistration(
                ProviderKey,
                static _ => new InMemoryObservationIngestionStore()));
    }
}
