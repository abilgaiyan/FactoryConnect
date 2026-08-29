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
                PersistenceProviderCapabilities.All,
                static _ =>
                {
                    var productionContextStore =
                        new InMemoryProductionContextProcessingStore();
                    var aggregationStore = new InMemoryMetricAggregationStore();
                    var projectionStore = new InMemoryOperationalMetricProjectionStore();

                    return new PersistenceProviderServices(
                        new InMemoryObservationIngestionStore(),
                        productionContextStore,
                        productionContextStore,
                        aggregationStore,
                        aggregationStore,
                        aggregationStore,
                        projectionStore,
                        projectionStore,
                        projectionStore);
                }));
    }
}
