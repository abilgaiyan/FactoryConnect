using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Infrastructure;
using FactoryConnect.Persistence;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class CurrentStatePersistenceCapabilityTests
{
    [Fact]
    public void CurrentStateCapabilityIsNotIncludedInLegacyAggregates()
    {
        Assert.Equal(
            PersistenceProviderCapabilities.None,
            PersistenceProviderCapabilities.Core &
                PersistenceProviderCapabilities.CurrentStateAuthorityReading);
        Assert.Equal(
            PersistenceProviderCapabilities.None,
            PersistenceProviderCapabilities.All &
                PersistenceProviderCapabilities.CurrentStateAuthorityReading);
    }

    [Fact]
    public void InMemoryProviderExplicitlyDeclaresCurrentStateCapability()
    {
        ServiceCollection services = new();
        services.AddInMemoryPersistenceProvider();

        var registration = Assert.Single(
            services
                .Where(descriptor => descriptor.ServiceType ==
                    typeof(IPersistenceProviderRegistration))
                .Select(descriptor =>
                    Assert.IsAssignableFrom<IPersistenceProviderRegistration>(
                        descriptor.ImplementationInstance)));

        Assert.NotEqual(
            PersistenceProviderCapabilities.None,
            registration.Capabilities &
                PersistenceProviderCapabilities.CurrentStateAuthorityReading);
    }

    [Fact]
    public void InMemoryProviderDoesNotExposeCurrentStateUnlessRequested()
    {
        ServiceCollection services = new();
        services.AddInMemoryPersistenceProvider();
        services.AddFactoryConnectPersistence(BuildConfiguration("InMemory"));

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IObservationIngestionStore>());
        Assert.Null(provider.GetService<ICurrentStateAuthorityCutProvider>());
    }

    [Fact]
    public void InMemoryRequestedCurrentStateCapabilityActivatesExactProviderOwnedService()
    {
        ServiceCollection services = new();
        services.AddInMemoryPersistenceProvider();
        services.AddFactoryConnectPersistence(
            BuildConfiguration("InMemory"),
            PersistenceProviderCapabilities.CurrentStateAuthorityReading);

        using var provider = services.BuildServiceProvider();

        var providerServices =
            provider.GetRequiredService<PersistenceProviderServices>();
        var activated =
            provider.GetRequiredService<ICurrentStateAuthorityCutProvider>();

        Assert.NotNull(providerServices.CurrentStateAuthorityCutProvider);
        Assert.Same(providerServices.CurrentStateAuthorityCutProvider, activated);
    }

    [Fact]
    public void ExistingCurrentStateRegistrationCausesFinalizationCollision()
    {
        ServiceCollection services = new();
        services.AddSingleton<ICurrentStateAuthorityCutProvider>(
            new StubCurrentStateAuthorityCutProvider());
        services.AddInMemoryPersistenceProvider();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddFactoryConnectPersistence(
                BuildConfiguration("InMemory"),
                PersistenceProviderCapabilities.CurrentStateAuthorityReading));

        Assert.Contains(
            nameof(ICurrentStateAuthorityCutProvider),
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SqlServerExplicitlyDeclaresCurrentStateCapability()
    {
        var configuration = BuildSqlServerConfiguration();
        ServiceCollection services = new();
        services.AddSqlServerPersistenceProvider(configuration);

        var registration = Assert.Single(
            services
                .Where(descriptor => descriptor.ServiceType ==
                    typeof(IPersistenceProviderRegistration))
                .Select(descriptor =>
                    Assert.IsAssignableFrom<IPersistenceProviderRegistration>(
                        descriptor.ImplementationInstance)));

        Assert.NotEqual(
            PersistenceProviderCapabilities.None,
            registration.Capabilities &
                PersistenceProviderCapabilities.CurrentStateAuthorityReading);
    }

    [Fact]
    public void SqlServerRequestedCurrentStateCapabilityPublishesProviderOwnedGraph()
    {
        var configuration = BuildSqlServerConfiguration();
        ServiceCollection services = new();
        services.AddSqlServerPersistenceProvider(configuration);
        services.AddFactoryConnectPersistence(
            configuration,
            PersistenceProviderCapabilities.CurrentStateAuthorityReading);

        using var provider = services.BuildServiceProvider();

        var providerServices = provider.GetRequiredService<PersistenceProviderServices>();
        Assert.Same(
            providerServices.CurrentStateAuthorityCutProvider,
            provider.GetRequiredService<ICurrentStateAuthorityCutProvider>());
        Assert.Same(
            providerServices.MappingCoverageAuthorityStore,
            provider.GetRequiredService<IMappingCoverageAuthorityStore>());
        Assert.Same(
            providerServices.MachineStateActivityAuthorityStore,
            provider.GetRequiredService<IMachineStateActivityAuthorityStore>());
    }

    [Fact]
    public void SqlServerWithoutCurrentStateRequirementRemainsCompatible()
    {
        var configuration = BuildSqlServerConfiguration();
        ServiceCollection services = new();
        services.AddSqlServerPersistenceProvider(configuration);
        services.AddFactoryConnectPersistence(configuration);

        using var provider = services.BuildServiceProvider();

        var providerServices =
            provider.GetRequiredService<PersistenceProviderServices>();
        Assert.Null(providerServices.CurrentStateAuthorityCutProvider);
        Assert.Null(providerServices.MappingCoverageAuthorityStore);
        Assert.Null(providerServices.MachineStateActivityAuthorityStore);
        Assert.Null(provider.GetService<ICurrentStateAuthorityCutProvider>());
    }

    [Fact]
    public void RequestedCurrentStateCapabilityActivatesProviderOwnedService()
    {
        var authorityProvider = new StubCurrentStateAuthorityCutProvider();
        ServiceCollection services = new();
        services.AddPersistenceProvider(
            new PersistenceProviderRegistration(
                "current-state",
                PersistenceProviderCapabilities.Core |
                    PersistenceProviderCapabilities.CurrentStateAuthorityReading,
                _ => CreateProviderServices(authorityProvider)));
        services.AddFactoryConnectPersistence(
            BuildConfiguration("current-state"),
            PersistenceProviderCapabilities.CurrentStateAuthorityReading);

        using var provider = services.BuildServiceProvider();

        Assert.Same(
            authorityProvider,
            provider.GetRequiredService<ICurrentStateAuthorityCutProvider>());
    }

    [Fact]
    public void DeclaredCurrentStateCapabilityWithoutServiceFailsOnResolution()
    {
        ServiceCollection services = new();
        services.AddPersistenceProvider(
            new PersistenceProviderRegistration(
                "current-state",
                PersistenceProviderCapabilities.Core |
                    PersistenceProviderCapabilities.CurrentStateAuthorityReading,
                _ => CreateProviderServices(null)));
        services.AddFactoryConnectPersistence(
            BuildConfiguration("current-state"),
            PersistenceProviderCapabilities.CurrentStateAuthorityReading);

        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            provider.GetRequiredService<ICurrentStateAuthorityCutProvider>());
        Assert.Contains(
            nameof(ICurrentStateAuthorityCutProvider),
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderServiceIsNotActivatedUnlessCurrentStateCapabilityIsRequested()
    {
        var authorityProvider = new StubCurrentStateAuthorityCutProvider();
        ServiceCollection services = new();
        services.AddPersistenceProvider(
            new PersistenceProviderRegistration(
                "current-state",
                PersistenceProviderCapabilities.Core |
                    PersistenceProviderCapabilities.CurrentStateAuthorityReading,
                _ => CreateProviderServices(authorityProvider)));
        services.AddFactoryConnectPersistence(BuildConfiguration("current-state"));

        using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetService<ICurrentStateAuthorityCutProvider>());
    }

    private static IConfiguration BuildSqlServerConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Persistence:Provider"] = "SqlServer",
                    ["ConnectionString"] =
                        "Server=(local);Database=FactoryConnect;Integrated Security=true;TrustServerCertificate=true",
                })
            .Build();

    private static IConfiguration BuildConfiguration(string provider) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Persistence:Provider"] = provider,
                })
            .Build();

    private static PersistenceProviderServices CreateProviderServices(
        ICurrentStateAuthorityCutProvider? currentStateAuthorityCutProvider)
    {
        var productionStore = new InMemoryProductionContextProcessingStore();

        return new PersistenceProviderServices(
            new InMemoryObservationIngestionStore(),
            productionStore,
            productionStore,
            new InMemoryMetricAggregationStore(),
            currentStateAuthorityCutProvider: currentStateAuthorityCutProvider);
    }

    private sealed class StubCurrentStateAuthorityCutProvider :
        ICurrentStateAuthorityCutProvider
    {
        public Task<CurrentStateAuthorityCutReadResult> ReadAuthorityCutAsync(
            CurrentStateAuthorityBinding binding,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
