using FactoryConnect.Abstractions;
using FactoryConnect.Core;
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
                PersistenceProviderCapabilities.All |
                    PersistenceProviderCapabilities.CurrentStateAuthorityReading,
                static _ =>
                {
                    var observationStore =
                        new InMemoryObservationIngestionStore();
                    var productionContextStore =
                        new InMemoryProductionContextProcessingStore();
                    var aggregationStore = new InMemoryMetricAggregationStore();
                    var projectionStore = new InMemoryOperationalMetricProjectionStore();
                    var rosterStore = new InMemoryMachineShiftOccurrenceRosterStore();
                    var mappingCoverageAuthorityStore =
                        new InMemoryMappingCoverageAuthorityStore();
                    var machineStateActivityAuthorityStore =
                        new InMemoryMachineStateActivityAuthorityStore();
                    var productionContextActivityReader =
                        new JointProductionContextActivityReader(
                            machineStateActivityAuthorityStore,
                            new ObservationProcessorId("machine-state-activity"));
                    var currentStateAuthorityCutProvider =
                        new InMemoryCurrentStateAuthorityCutProvider(
                            observationStore,
                            mappingCoverageAuthorityStore,
                            machineStateActivityAuthorityStore);

                    return new PersistenceProviderServices(
                        observationStore,
                        productionContextStore,
                        productionContextStore,
                        aggregationStore,
                        aggregationStore,
                        aggregationStore,
                        projectionStore,
                        projectionStore,
                        projectionStore,
                        rosterStore,
                        currentStateAuthorityCutProvider:
                            currentStateAuthorityCutProvider,
                        mappingCoverageAuthorityStore: mappingCoverageAuthorityStore,
                        machineStateActivityAuthorityStore:
                            machineStateActivityAuthorityStore,
                        productionContextActivityReader:
                            productionContextActivityReader);
                }));
    }
}
