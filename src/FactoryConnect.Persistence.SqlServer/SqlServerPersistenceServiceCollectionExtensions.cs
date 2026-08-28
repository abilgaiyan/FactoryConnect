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

        return services.AddPersistenceProvider(
            new PersistenceProviderRegistration(
                ProviderKey,
                PersistenceProviderCapabilities.Core,
                _ =>
                {
                    var options = new SqlServerPersistenceOptions();
                    configuration.Bind(options);
                    var connectionString =
                        options.GetRequiredConnectionString();

                    throw new InvalidOperationException(
                        "SQL Server persistence does not yet provide FC-027 operational metric durability capabilities.");
                }));
    }
}
