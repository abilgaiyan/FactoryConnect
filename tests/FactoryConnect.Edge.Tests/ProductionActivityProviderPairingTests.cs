using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Edge;
using FactoryConnect.Infrastructure;
using FactoryConnect.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class ProductionActivityProviderPairingTests
{
    [Fact]
    public void ProviderProductionReaderWithoutAuthorityFailsClosed()
    {
        var observationStore = new InMemoryObservationIngestionStore();
        var productionStore = new InMemoryProductionContextProcessingStore();
        var aggregationStore = new InMemoryMetricAggregationStore();
        var readerStore = new InMemoryMachineStateActivityAuthorityStore();
        var reader = new JointProductionContextActivityReader(
            readerStore,
            new ObservationProcessorId("machine-state-activity"));
        var providerServices = new PersistenceProviderServices(
            observationStore,
            productionStore,
            productionStore,
            aggregationStore,
            productionContextActivityReader: reader);
        var services = new ServiceCollection();
        services.AddSingleton(providerServices);
        services.AddSingleton<ObservationAuthorityStoreGraph>();

        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<ObservationAuthorityStoreGraph>());

        Assert.Contains(
            "production activity reader without the matching state/activity authority store",
            exception.Message,
            StringComparison.Ordinal);
    }
}
