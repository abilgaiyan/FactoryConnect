using FactoryConnect.Infrastructure;
using FactoryConnect.Persistence;
using FactoryConnect.Persistence.SqlServer;
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

        services.AddInMemoryPersistenceProvider();
        services.AddSqlServerPersistenceProvider(
            configuration.GetSection(
                SqlServerPersistenceOptions.SectionName));

        services.AddFactoryConnectPersistence(configuration, requiredCapabilities);

        return services;
    }
}
