using FactoryConnect.Infrastructure;
using FactoryConnect.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.Edge;

public static class EdgePersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddFactoryConnectEdgePersistence(
        this IServiceCollection services,
        IConfiguration configuration,
        PersistenceProviderCapabilities requiredCapabilities = PersistenceProviderCapabilities.Core)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddFactoryConnectPersistenceProviders(configuration);
        services.AddFactoryConnectPersistence(configuration, requiredCapabilities);

        return services;
    }
}
