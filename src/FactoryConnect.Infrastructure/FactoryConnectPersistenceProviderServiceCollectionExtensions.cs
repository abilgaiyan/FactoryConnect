using FactoryConnect.Persistence.SqlServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.Infrastructure;

public static class FactoryConnectPersistenceProviderServiceCollectionExtensions
{
    public static IServiceCollection AddFactoryConnectPersistenceProviders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddInMemoryPersistenceProvider();
        services.AddSqlServerPersistenceProvider(
            configuration.GetSection(SqlServerPersistenceOptions.SectionName));

        return services;
    }
}
