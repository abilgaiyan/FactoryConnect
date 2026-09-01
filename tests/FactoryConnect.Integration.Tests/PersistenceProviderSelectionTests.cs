using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Infrastructure;
using FactoryConnect.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class PersistenceProviderSelectionTests
{
    [Fact]
    public void MissingProviderConfigurationFailsFast()
    {
        ServiceCollection services = new();
        var configuration = BuildConfiguration();
        services.AddInMemoryPersistenceProvider();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddFactoryConnectPersistence(configuration));

        Assert.Equal("Persistence:Provider is required.", exception.Message);
    }

    [Fact]
    public void UnknownProviderFailsFast()
    {
        ServiceCollection services = new();
        var configuration = BuildConfiguration("Unknown");
        services.AddInMemoryPersistenceProvider();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddFactoryConnectPersistence(configuration));

        Assert.Equal("Persistence provider 'UNKNOWN' is not registered.", exception.Message);
    }

    [Fact]
    public void NoRegisteredProvidersFailsFast()
    {
        ServiceCollection services = new();
        var configuration = BuildConfiguration("InMemory");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddFactoryConnectPersistence(configuration));

        Assert.Equal(
            "At least one persistence provider must be registered before persistence finalization.",
            exception.Message);
    }

    [Fact]
    public void DuplicateProviderKeysFailFast()
    {
        ServiceCollection services = new();
        var configuration = BuildConfiguration("InMemory");
        services.AddInMemoryPersistenceProvider();
        services.AddPersistenceProvider(
            new PersistenceProviderRegistration(
                " inMemory ",
                PersistenceProviderCapabilities.All,
                _ => CreateProviderServices(new InMemoryObservationIngestionStore())));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddFactoryConnectPersistence(configuration));

        Assert.Equal(
            "Persistence provider key 'INMEMORY' is registered more than once.",
            exception.Message);
    }

    [Fact]
    public void ProviderRegistrationAfterFinalizationFailsFast()
    {
        ServiceCollection services = new();
        var configuration = BuildConfiguration("InMemory");
        services.AddInMemoryPersistenceProvider();
        services.AddFactoryConnectPersistence(configuration);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddPersistenceProvider(
                new PersistenceProviderRegistration(
                    "other",
                    PersistenceProviderCapabilities.Core,
                    _ => CreateProviderServices(new InMemoryObservationIngestionStore()))));

        Assert.Equal(
            "Persistence has already been finalized. Register providers before AddFactoryConnectPersistence.",
            exception.Message);
    }

    [Fact]
    public void ExistingObservationStoreBeforeFinalizationFailsFast()
    {
        ServiceCollection services = new();
        services.AddSingleton<IObservationIngestionStore, InMemoryObservationIngestionStore>();
        var configuration = BuildConfiguration("InMemory");
        services.AddInMemoryPersistenceProvider();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddFactoryConnectPersistence(configuration));

        Assert.Contains(nameof(IObservationIngestionStore), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingProductionContextStoreBeforeFinalizationFailsFast()
    {
        ServiceCollection services = new();
        services.AddSingleton<IProductionContextProcessingStore, InMemoryProductionContextProcessingStore>();
        var configuration = BuildConfiguration("InMemory");
        services.AddInMemoryPersistenceProvider();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddFactoryConnectPersistence(configuration));

        Assert.Contains(nameof(IProductionContextProcessingStore), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingMetricInputReaderBeforeFinalizationFailsFast()
    {
        ServiceCollection services = new();
        services.AddSingleton<IMetricInputReader, InMemoryProductionContextProcessingStore>();
        var configuration = BuildConfiguration("InMemory");
        services.AddInMemoryPersistenceProvider();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddFactoryConnectPersistence(configuration));

        Assert.Contains(nameof(IMetricInputReader), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingMetricAggregationStoreBeforeFinalizationFailsFast()
    {
        ServiceCollection services = new();
        services.AddSingleton<IMetricAggregationStore, InMemoryMetricAggregationStore>();
        var configuration = BuildConfiguration("InMemory");
        services.AddInMemoryPersistenceProvider();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddFactoryConnectPersistence(configuration));

        Assert.Contains(nameof(IMetricAggregationStore), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingMachineShiftOccurrenceRosterStoreBeforeFinalizationFailsFast()
    {
        ServiceCollection services = new();
        services.AddSingleton<IMachineShiftOccurrenceRosterStore,
            InMemoryMachineShiftOccurrenceRosterStore>();
        var configuration = BuildConfiguration("InMemory");
        services.AddInMemoryPersistenceProvider();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddFactoryConnectPersistence(configuration));

        Assert.Contains(
            nameof(IMachineShiftOccurrenceRosterStore),
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InMemoryProviderActivatesMachineShiftOccurrenceRosterCapability()
    {
        ServiceCollection services = new();
        var configuration = BuildConfiguration("InMemory");
        services.AddInMemoryPersistenceProvider();
        services.AddFactoryConnectPersistence(
            configuration,
            PersistenceProviderCapabilities.MachineShiftOccurrenceRoster);

        using var provider = services.BuildServiceProvider();

        Assert.IsType<InMemoryMachineShiftOccurrenceRosterStore>(
            provider.GetRequiredService<IMachineShiftOccurrenceRosterStore>());
    }

    [Fact]
    public void DeclaredRosterCapabilityWithoutProviderServiceFailsOnResolution()
    {
        ServiceCollection services = new();
        var configuration = BuildConfiguration("custom");
        services.AddPersistenceProvider(
            new CustomPersistenceProviderRegistration(
                "custom",
                PersistenceProviderCapabilities.Core |
                    PersistenceProviderCapabilities.MachineShiftOccurrenceRoster,
                _ => CreateProviderServices(new InMemoryObservationIngestionStore())));
        services.AddFactoryConnectPersistence(
            configuration,
            PersistenceProviderCapabilities.MachineShiftOccurrenceRoster);

        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            provider.GetRequiredService<IMachineShiftOccurrenceRosterStore>());
        Assert.Contains(
            nameof(IMachineShiftOccurrenceRosterStore),
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SelectedProviderOwnsActivatedCapabilities()
    {
        ServiceCollection services = new();
        var configuration = BuildConfiguration("custom");
        var observationStore = new InMemoryObservationIngestionStore();
        services.AddPersistenceProvider(
            new CustomPersistenceProviderRegistration(
                "custom",
                PersistenceProviderCapabilities.Core,
                _ => CreateProviderServices(observationStore)));
        services.AddFactoryConnectPersistence(configuration);

        using var provider = services.BuildServiceProvider();

        Assert.Same(observationStore, provider.GetRequiredService<IObservationIngestionStore>());
    }

    [Fact]
    public void SelectedProviderFactoryIsLazy()
    {
        ServiceCollection services = new();
        var configuration = BuildConfiguration("custom");
        var factoryCalls = 0;
        services.AddPersistenceProvider(
            new CustomPersistenceProviderRegistration(
                "custom",
                PersistenceProviderCapabilities.Core,
                _ =>
                {
                    factoryCalls++;
                    return CreateProviderServices(new InMemoryObservationIngestionStore());
                }));
        services.AddFactoryConnectPersistence(configuration);

        Assert.Equal(0, factoryCalls);

        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IObservationIngestionStore>();

        Assert.Equal(1, factoryCalls);
    }

    [Fact]
    public void UnselectedProviderFactoryIsNeverInvoked()
    {
        ServiceCollection services = new();
        var configuration = BuildConfiguration("InMemory");
        var customFactoryCalled = false;
        services.AddInMemoryPersistenceProvider();
        services.AddPersistenceProvider(
            new CustomPersistenceProviderRegistration(
                "custom",
                PersistenceProviderCapabilities.Core,
                _ =>
                {
                    customFactoryCalled = true;
                    return CreateProviderServices(new InMemoryObservationIngestionStore());
                }));
        services.AddFactoryConnectPersistence(configuration);

        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IObservationIngestionStore>();

        Assert.False(customFactoryCalled);
    }

    [Fact]
    public void ProviderKeySelectionIsCaseInsensitive()
    {
        ServiceCollection services = new();
        var configuration = BuildConfiguration(" InMemory ");

        services.AddInMemoryPersistenceProvider();
        services.AddFactoryConnectPersistence(configuration);

        using var provider = services.BuildServiceProvider();

        var store = provider.GetRequiredService<IObservationIngestionStore>();

        Assert.IsType<InMemoryObservationIngestionStore>(store);
    }

    private static IConfiguration BuildConfiguration(string? provider = null)
    {
        Dictionary<string, string?> values = [];

        if (provider is not null)
        {
            values["Persistence:Provider"] = provider;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private static PersistenceProviderServices CreateProviderServices(
        IObservationIngestionStore observationStore)
    {
        var productionStore = new InMemoryProductionContextProcessingStore();

        return new PersistenceProviderServices(
            observationStore,
            productionStore,
            productionStore,
            new InMemoryMetricAggregationStore());
    }

    private sealed class CustomPersistenceProviderRegistration :
        IPersistenceProviderRegistration
    {
        private readonly Func<
            IServiceProvider,
            PersistenceProviderServices> _factory;

        public CustomPersistenceProviderRegistration(
            string providerKey,
            PersistenceProviderCapabilities capabilities,
            Func<IServiceProvider, PersistenceProviderServices> factory)
        {
            ProviderKey = providerKey;
            Capabilities = capabilities;
            _factory = factory;
        }

        public string ProviderKey { get; }

        public PersistenceProviderCapabilities Capabilities { get; }

        public PersistenceProviderServices Create(IServiceProvider services) =>
            _factory(services);
    }
}
