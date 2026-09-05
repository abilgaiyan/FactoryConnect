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

        var configurationSnapshot = new Lazy<SqlServerPersistenceConfigurationSnapshot>(
            () => SqlServerPersistenceConfigurationSnapshot.Create(configuration),
            LazyThreadSafetyMode.ExecutionAndPublication);

        return services.AddPersistenceProvider(
            new PersistenceProviderRegistration(
                ProviderKey,
                PersistenceProviderCapabilities.Core,
                _ =>
                {
                    var snapshot = configurationSnapshot.Value;
                    var connectionString = snapshot.ConnectionString;

                    return new PersistenceProviderServices(
                        new SqlServerObservationIngestionStore(connectionString),
                        new SqlServerProductionContextProcessingStore(connectionString),
                        new SqlServerMetricInputStore(connectionString),
                        new SqlServerMetricAggregationStore(connectionString));
                },
                _ =>
                {
                    var snapshot = configurationSnapshot.Value;

                    return new SqlServerPersistenceStartupGate(
                        snapshot.ConnectionString,
                        snapshot.StartupOptions);
                }));
    }

    private sealed record SqlServerPersistenceConfigurationSnapshot(
        string ConnectionString,
        SqlPersistenceStartupOptions StartupOptions)
    {
        public static SqlServerPersistenceConfigurationSnapshot Create(
            IConfiguration configuration)
        {
            var options = new SqlServerPersistenceOptions();
            configuration.Bind(options);

            return new SqlServerPersistenceConfigurationSnapshot(
                options.GetRequiredConnectionString(),
                options.GetStartupOptions());
        }
    }
}
