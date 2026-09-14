using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Infrastructure;
using FactoryConnect.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class InMemoryAuthorityInstanceGraphConformanceTests
{
    [Fact]
    public void FinalizedInMemoryProviderProjectsExactOwnedAuthorityInstances()
    {
        ServiceCollection services = new();
        services.AddInMemoryPersistenceProvider();
        services.AddFactoryConnectPersistence(BuildConfiguration("InMemory"));

        using var provider = services.BuildServiceProvider();

        var providerServices = provider.GetRequiredService<PersistenceProviderServices>();
        var acquisition = provider.GetRequiredService<IObservationIngestionStore>();
        var mapping = provider.GetRequiredService<IMappingCoverageAuthorityStore>();
        var stateActivity = provider.GetRequiredService<IMachineStateActivityAuthorityStore>();

        Assert.Same(providerServices.ObservationIngestionStore, acquisition);
        Assert.Same(providerServices.MappingCoverageAuthorityStore, mapping);
        Assert.Same(providerServices.MachineStateActivityAuthorityStore, stateActivity);
        Assert.IsType<InMemoryObservationIngestionStore>(acquisition);
        Assert.IsType<InMemoryMappingCoverageAuthorityStore>(mapping);
        Assert.IsType<InMemoryMachineStateActivityAuthorityStore>(stateActivity);
        Assert.NotSame(acquisition, mapping);
        Assert.NotSame(acquisition, stateActivity);
        Assert.NotSame(mapping, stateActivity);
    }

    [Fact]
    public void FinalizedInternalAuthorityRolesHaveSelectedProviderSingletonLifetime()
    {
        ServiceCollection services = new();
        services.AddInMemoryPersistenceProvider();
        services.AddFactoryConnectPersistence(BuildConfiguration("InMemory"));

        using var provider = services.BuildServiceProvider();

        var providerServices = provider.GetRequiredService<PersistenceProviderServices>();

        Assert.Same(
            providerServices,
            provider.GetRequiredService<PersistenceProviderServices>());
        Assert.Same(
            providerServices.MappingCoverageAuthorityStore,
            provider.GetRequiredService<IMappingCoverageAuthorityStore>());
        Assert.Same(
            provider.GetRequiredService<IMappingCoverageAuthorityStore>(),
            provider.GetRequiredService<IMappingCoverageAuthorityStore>());
        Assert.Same(
            providerServices.MachineStateActivityAuthorityStore,
            provider.GetRequiredService<IMachineStateActivityAuthorityStore>());
        Assert.Same(
            provider.GetRequiredService<IMachineStateActivityAuthorityStore>(),
            provider.GetRequiredService<IMachineStateActivityAuthorityStore>());
    }

    [Fact]
    public void InternalAuthorityRolesRemainOptionalForExistingProviderConstruction()
    {
        var productionStore = new InMemoryProductionContextProcessingStore();
        var providerServices = new PersistenceProviderServices(
            new InMemoryObservationIngestionStore(),
            productionStore,
            productionStore,
            new InMemoryMetricAggregationStore());

        Assert.Null(providerServices.MappingCoverageAuthorityStore);
        Assert.Null(providerServices.MachineStateActivityAuthorityStore);
        Assert.Null(providerServices.CurrentStateAuthorityCutProvider);
    }

    [Fact]
    public void NullInternalAuthorityRolesRemainUnresolvedAfterFinalization()
    {
        ServiceCollection services = new();
        services.AddPersistenceProvider(
            new PersistenceProviderRegistration(
                "legacy",
                PersistenceProviderCapabilities.Core,
                static _ => CreateLegacyProviderServices()));
        services.AddFactoryConnectPersistence(BuildConfiguration("legacy"));

        using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetService<IMappingCoverageAuthorityStore>());
        Assert.Null(provider.GetService<IMachineStateActivityAuthorityStore>());
        Assert.NotNull(provider.GetRequiredService<IObservationIngestionStore>());
    }

    [Fact]
    public void InternalAuthorityRolesDoNotParticipateInCapabilityCollisionDetection()
    {
        var priorMapping = new InMemoryMappingCoverageAuthorityStore();
        var priorStateActivity = new InMemoryMachineStateActivityAuthorityStore();
        ServiceCollection services = new();
        services.AddSingleton<IMappingCoverageAuthorityStore>(priorMapping);
        services.AddSingleton<IMachineStateActivityAuthorityStore>(priorStateActivity);
        services.AddInMemoryPersistenceProvider();

        services.AddFactoryConnectPersistence(BuildConfiguration("InMemory"));

        using var provider = services.BuildServiceProvider();
        var providerServices = provider.GetRequiredService<PersistenceProviderServices>();
        var mapping = provider.GetRequiredService<IMappingCoverageAuthorityStore>();
        var stateActivity = provider.GetRequiredService<IMachineStateActivityAuthorityStore>();

        Assert.Same(providerServices.MappingCoverageAuthorityStore, mapping);
        Assert.Same(providerServices.MachineStateActivityAuthorityStore, stateActivity);
        Assert.NotSame(priorMapping, mapping);
        Assert.NotSame(priorStateActivity, stateActivity);
    }

    [Fact]
    public void InMemoryProviderDoesNotAdvertiseCurrentStateAuthorityDuring2EA()
    {
        Assert.Equal(
            PersistenceProviderCapabilities.None,
            PersistenceProviderCapabilities.All &
                PersistenceProviderCapabilities.CurrentStateAuthorityReading);

        ServiceCollection services = new();
        services.AddInMemoryPersistenceProvider();
        services.AddFactoryConnectPersistence(BuildConfiguration("InMemory"));

        using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetService<ICurrentStateAuthorityCutProvider>());
    }

    private static PersistenceProviderServices CreateLegacyProviderServices()
    {
        var productionStore = new InMemoryProductionContextProcessingStore();

        return new PersistenceProviderServices(
            new InMemoryObservationIngestionStore(),
            productionStore,
            productionStore,
            new InMemoryMetricAggregationStore());
    }

    private static IConfiguration BuildConfiguration(string provider) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Persistence:Provider"] = provider,
                })
            .Build();
}
