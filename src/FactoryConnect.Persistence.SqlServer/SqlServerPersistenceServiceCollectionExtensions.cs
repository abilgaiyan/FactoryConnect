using FactoryConnect.Abstractions;
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
                PersistenceProviderCapabilities.Core |
                PersistenceProviderCapabilities.OperationalMetricProjectionQuery |
                PersistenceProviderCapabilities.OperationalMetricReportingQuery |
                PersistenceProviderCapabilities.MachineShiftOccurrenceRoster |
                PersistenceProviderCapabilities.CurrentStateAuthorityReading,
                _ =>
                {
                    var snapshot = configurationSnapshot.Value;
                    var connectionString = snapshot.ConnectionString;
                    var observationStore =
                        new SqlServerObservationIngestionStore(connectionString);
                    var currentStateRequested = services.Any(
                        static descriptor =>
                            descriptor.ServiceType ==
                            typeof(ICurrentStateAuthorityCutProvider));
                    var mappingStore = currentStateRequested
                        ? new SqlServerMappingCoverageAuthorityStore(connectionString)
                        : null;
                    var stateActivityStore = currentStateRequested
                        ? new SqlServerMachineStateActivityAuthorityStore(connectionString)
                        : null;
                    var authorityCutProvider = currentStateRequested
                        ? new SqlServerCurrentStateAuthorityCutProvider(connectionString)
                        : null;

                    return new PersistenceProviderServices(
                        observationStore,
                        new SqlServerProductionContextProcessingStore(connectionString),
                        new SqlServerMetricInputStore(connectionString),
                        new SqlServerMetricAggregationStore(connectionString),
                        operationalMetricProjectionQueryReader:
                            new SqlServerOperationalMetricProjectionQueryReader(connectionString),
                        operationalMetricReportingQueryProvider:
                            new SqlServerOperationalMetricReportingQueryProvider(connectionString),
                        machineShiftOccurrenceRosterStore:
                            new SqlServerMachineShiftOccurrenceRosterStore(connectionString),
                        currentStateAuthorityCutProvider: authorityCutProvider,
                        mappingCoverageAuthorityStore: mappingStore,
                        machineStateActivityAuthorityStore: stateActivityStore);
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
