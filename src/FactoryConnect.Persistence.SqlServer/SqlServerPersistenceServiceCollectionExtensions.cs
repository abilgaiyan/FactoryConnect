using FactoryConnect.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.Persistence.SqlServer;

public static class SqlServerPersistenceServiceCollectionExtensions
{
    public const string ProviderKey = "SqlServer";

    public static IServiceCollection AddSqlServerPersistenceProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new SqlServerPersistenceOptions();
        configuration.Bind(options);
        var connectionString = options.GetRequiredConnectionString();

        return services.AddPersistenceProvider(
            new PersistenceProviderRegistration(
                ProviderKey,
                _ => new SqlServerObservationIngestionStore(
                    connectionString)));
    }
}
