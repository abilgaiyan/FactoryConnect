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
    public void ProviderKeyNormalizesWhitespaceAndCase()
    {
        var key = PersistenceProviderKey.Normalize("  inMemory  ");

        Assert.Equal("INMEMORY", key);
    }

    [Fact]
    public void AddFactoryConnectPersistenceRequiresProviderConfiguration()
    {
        ServiceCollection services = new();
        services.AddInMemoryPersistenceProvider();
        var configuration = BuildConfiguration();

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddFactoryConnectPersistence(configuration));

        Assert.Equal("Persistence:Provider is required.", exception.Message);
    }

    [Fact]
    public void AddFactoryConnectPersistenceRequiresAvailableProvider()
    {
        ServiceCollection services = new();
        var configuration = BuildConfiguration("inmemory");

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddFactoryConnectPersistence(configuration));

        Assert.Equal(
            "At least one persistence provider must be registered " +
            "before persistence finalization.",
            exception.Message);
    }

    [Fact]
    public void ResolvingStoreActivatesOnlySelectedProvider()
    {
        ServiceCollection services = new();
        var configuration = BuildConfiguration("primary");
        var primaryActivations = 0;
        var secondaryActivations = 0;

        services.AddPersistenceProvider(
            new PersistenceProviderRegistration(
                "Primary",
                _ =>
                {
                    primaryActivations++;
                    return CreateProviderServices(
                        new InMemoryObservationIngestionStore());
                }));
        services.AddPersistenceProvider(
            new PersistenceProviderRegistration(
                "Secondary",
                _ =>
                {
                    secondaryActivations++;
                    return CreateProviderServices(
                        new InMemoryObservationIngestionStore());
                }));
        services.AddFactoryConnectPersistence(configuration);

        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IObservationIngestionStore>();
        var second = provider.GetRequiredService<IObservationIngestionStore>();

        Assert.Same(first, second);
        Assert.Equal(1, primaryActivations);
        Assert.Equal(0, secondaryActivations);
    }

    [Fact]
    public void RegisteredProviderIsNotActivatedDuringFinalization()
    {
        ServiceCollection services = new();
        var configuration = BuildConfiguration("primary");
        var activations = 0;

        services.AddPersistenceProvider(
            new PersistenceProviderRegistration(
                "Primary",
                _ =>
                {
                    activations++;
                    return CreateProviderServices(
                        new InMemoryObservationIngestionStore());
                }));
        services.AddFactoryConnectPersistence(configuration);

        using var provider = services.BuildServiceProvider();

        Assert.Equal(0, activations);
    }

    [Fact]
    public void CustomProviderWithUnnormalizedKeyCanBeSelected()
    {
        ServiceCollection services = new();
        var configuration = BuildConfiguration("SqlServer");

        services.AddPersistenceProvider(
            new CustomPersistenceProviderRegistration(
                " sqlserver ",
                static _ => CreateProviderServices(
                    new InMemoryObservationIngestionStore())));
        services.AddFactoryConnectPersistence(configuration);

        using var provider = services.BuildServiceProvider();

        var store = provider.GetRequiredService<IObservationIngestionStore>();

        Assert.IsType<InMemoryObservationIngestionStore>(store);
    }

    [Fact]
    public void FinalizationRejectsUnknownProvider()
    {
        ServiceCollection services = new();
        var configuration = BuildConfiguration("missing");

        services.AddInMemoryPersistenceProvider();

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddFactoryConnectPersistence(configuration));

        Assert.Equal(
            "Persistence provider 'MISSING' is not registered.",
            exception.Message);
    }

    [Fact]
    public void FinalizationRejectsDuplicateNormalizedProviderKeys()
    {
        ServiceCollection services = new();
        var configuration = BuildConfiguration("primary");

        services.AddPersistenceProvider(
            new PersistenceProviderRegistration(
                "Primary",
                static _ => CreateProviderServices(
                    new InMemoryObservationIngestionStore())));
        services.AddPersistenceProvider(
            new PersistenceProviderRegistration(
                " primary ",
                static _ => CreateProviderServices(
                    new InMemoryObservationIngestionStore())));

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddFactoryConnectPersistence(configuration));

        Assert.Equal(
            "Persistence provider key 'PRIMARY' is registered more than once.",
            exception.Message);
    }

    [Fact]
    public void FinalizationRejectsEquivalentCustomProviderKeys()
    {
        ServiceCollection services = new();
        var configuration = BuildConfiguration("SqlServer");

        services.AddPersistenceProvider(
            new CustomPersistenceProviderRegistration(
                "SqlServer",
                static _ => CreateProviderServices(
                    new InMemoryObservationIngestionStore())));
        services.AddPersistenceProvider(
            new CustomPersistenceProviderRegistration(
                " sqlserver ",
                static _ => CreateProviderServices(
                    new InMemoryObservationIngestionStore())));

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddFactoryConnectPersistence(configuration));

        Assert.Equal(
            "Persistence provider key 'SQLSERVER' is registered more than once.",
            exception.Message);
    }

    [Fact]
    public void FinalizationRejectsPreRegisteredStore()
    {
        ServiceCollection services = new();
        var configuration = BuildConfiguration("inmemory");

        services.AddInMemoryPersistenceProvider();
        services.AddSingleton<IObservationIngestionStore>(
            new InMemoryObservationIngestionStore());

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddFactoryConnectPersistence(configuration));

        Assert.Equal(
            "IObservationIngestionStore is already registered. " +
            "Persistence activation must own the single store registration.",
            exception.Message);
    }

    [Fact]
    public void ProviderRegistrationAfterFinalizationIsRejected()
    {
        ServiceCollection services = new();
        var configuration = BuildConfiguration("inmemory");

        services.AddInMemoryPersistenceProvider();
        services.AddFactoryConnectPersistence(configuration);

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddPersistenceProvider(
                new PersistenceProviderRegistration(
                    "Other",
                    static _ => CreateProviderServices(
                        new InMemoryObservationIngestionStore()))));

        Assert.Equal(
            "Persistence has already been finalized. " +
            "Register providers before AddFactoryConnectPersistence.",
            exception.Message);
    }

    [Fact]
    public void InMemoryProviderIsActivatedThroughProviderRegistration()
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

        public string ProviderKey { get; }

        public CustomPersistenceProviderRegistration(
            string providerKey,
            Func<IServiceProvider, PersistenceProviderServices> factory)
        {
            ProviderKey = providerKey;
            _factory = factory;
        }

        public PersistenceProviderServices Create(IServiceProvider services) =>
            _factory(services);
    }
}
