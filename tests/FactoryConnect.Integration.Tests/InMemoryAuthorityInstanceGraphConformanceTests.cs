using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Infrastructure;
using FactoryConnect.Persistence;
using FactoryConnect.Persistence.SqlServer;
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
        Assert.NotNull(provider.GetRequiredService<IProductionContextProcessingStore>());
        Assert.NotNull(provider.GetRequiredService<IMetricInputReader>());
        Assert.NotNull(provider.GetRequiredService<IMetricAggregationStore>());
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

    [Fact]
    public async Task ProviderOwnedAuthorityStoresShareOneLifetimeButKeepBindingsIsolated()
    {
        ServiceCollection services = new();
        services.AddInMemoryPersistenceProvider();
        services.AddFactoryConnectPersistence(BuildConfiguration("InMemory"));

        using var provider = services.BuildServiceProvider();
        var providerServices = provider.GetRequiredService<PersistenceProviderServices>();
        var mappingStore = provider.GetRequiredService<IMappingCoverageAuthorityStore>();
        var stateStore = provider.GetRequiredService<IMachineStateActivityAuthorityStore>();

        Assert.Same(providerServices.MappingCoverageAuthorityStore, mappingStore);
        Assert.Same(providerServices.MachineStateActivityAuthorityStore, stateStore);

        var mappingProcessor = new ObservationProcessorId("canonical-mapping");
        var stateProcessor = new ObservationProcessorId("machine-state-activity");
        var bindingA = CreateBinding(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "MTConnect:CNC-01",
            mappingProcessor,
            stateProcessor);
        var bindingB = CreateBinding(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            "MTConnect:CNC-02",
            mappingProcessor,
            stateProcessor);

        await mappingStore.CommitAsync(
            new MappingCoverageCommit(
                expectedAuthority: null,
                bindingA.MappingProcessorId,
                bindingA.ObservationStreamId,
                new ObservationPosition(11),
                new ObservationPosition(9)));

        var mappingA = await mappingStore.ReadAsync(
            bindingA.MappingProcessorId,
            bindingA.ObservationStreamId);
        var mappingBBefore = await mappingStore.ReadAsync(
            bindingB.MappingProcessorId,
            bindingB.ObservationStreamId);

        Assert.NotNull(mappingA);
        Assert.Equal(new ObservationPosition(11), mappingA.RawConsumedThrough);
        Assert.Equal(new ObservationPosition(9), mappingA.MappedEvaluationInputHighWater);
        Assert.Null(mappingBBefore);

        await mappingStore.CommitAsync(
            new MappingCoverageCommit(
                expectedAuthority: null,
                bindingB.MappingProcessorId,
                bindingB.ObservationStreamId,
                new ObservationPosition(21),
                new ObservationPosition(20)));

        var mappingAAfter = await mappingStore.ReadAsync(
            bindingA.MappingProcessorId,
            bindingA.ObservationStreamId);
        var mappingB = await mappingStore.ReadAsync(
            bindingB.MappingProcessorId,
            bindingB.ObservationStreamId);

        Assert.Equal(new ObservationPosition(11), mappingAAfter!.RawConsumedThrough);
        Assert.Equal(new ObservationPosition(21), mappingB!.RawConsumedThrough);

        var stateAResult = await stateStore.PublishAsync(
            CreateInitialStatePublication(bindingA, new ObservationPosition(9), MachineState.Running));
        Assert.IsType<MachineStateActivityAuthorityPublicationAccepted>(stateAResult);

        var stateA = await stateStore.ReadAsync(
            bindingA.StateProcessorId,
            bindingA.ObservationStreamId);
        var stateBBefore = await stateStore.ReadAsync(
            bindingB.StateProcessorId,
            bindingB.ObservationStreamId);

        Assert.NotNull(stateA);
        Assert.Equal(new ObservationPosition(9), stateA.Projection.Position);
        Assert.Equal(MachineState.Running, stateA.Projection.State);
        Assert.Null(stateBBefore);

        var stateBResult = await stateStore.PublishAsync(
            CreateInitialStatePublication(bindingB, new ObservationPosition(20), MachineState.Idle));
        Assert.IsType<MachineStateActivityAuthorityPublicationAccepted>(stateBResult);

        var stateAAfter = await stateStore.ReadAsync(
            bindingA.StateProcessorId,
            bindingA.ObservationStreamId);
        var stateB = await stateStore.ReadAsync(
            bindingB.StateProcessorId,
            bindingB.ObservationStreamId);

        Assert.Equal(new ObservationPosition(9), stateAAfter!.Projection.Position);
        Assert.Equal(MachineState.Running, stateAAfter.Projection.State);
        Assert.Equal(new ObservationPosition(20), stateB!.Projection.Position);
        Assert.Equal(MachineState.Idle, stateB.Projection.State);
    }

    [Fact]
    public void SqlServerProviderKeepsLegacyCapabilitiesAndNullInternalAuthorityRoles()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Persistence:Provider"] = SqlServerPersistenceServiceCollectionExtensions.ProviderKey,
                    ["ConnectionString"] = "Server=(local);Database=FactoryConnect;Integrated Security=true;TrustServerCertificate=true",
                })
            .Build();
        ServiceCollection services = new();
        services.AddSqlServerPersistenceProvider(configuration);

        var registration = Assert.Single(
            services
                .Where(descriptor => descriptor.ServiceType == typeof(IPersistenceProviderRegistration))
                .Select(descriptor => Assert.IsAssignableFrom<IPersistenceProviderRegistration>(
                    descriptor.ImplementationInstance)));
        var expectedCapabilities =
            PersistenceProviderCapabilities.Core |
            PersistenceProviderCapabilities.OperationalMetricProjectionQuery |
            PersistenceProviderCapabilities.OperationalMetricReportingQuery |
            PersistenceProviderCapabilities.MachineShiftOccurrenceRoster;

        Assert.Equal(expectedCapabilities, registration.Capabilities);
        Assert.Equal(
            PersistenceProviderCapabilities.None,
            registration.Capabilities & PersistenceProviderCapabilities.CurrentStateAuthorityReading);

        services.AddFactoryConnectPersistence(configuration);
        using var provider = services.BuildServiceProvider();
        var providerServices = provider.GetRequiredService<PersistenceProviderServices>();

        Assert.Null(providerServices.MappingCoverageAuthorityStore);
        Assert.Null(providerServices.MachineStateActivityAuthorityStore);
        Assert.Null(provider.GetService<IMappingCoverageAuthorityStore>());
        Assert.Null(provider.GetService<IMachineStateActivityAuthorityStore>());
        Assert.NotNull(provider.GetRequiredService<IObservationIngestionStore>());
        Assert.NotNull(provider.GetRequiredService<IProductionContextProcessingStore>());
        Assert.NotNull(provider.GetRequiredService<IMetricInputReader>());
        Assert.NotNull(provider.GetRequiredService<IMetricAggregationStore>());
    }

    private static CurrentStateAuthorityBinding CreateBinding(
        Guid machineId,
        string streamKey,
        ObservationProcessorId mappingProcessor,
        ObservationProcessorId stateProcessor)
    {
        var machine = new MachineId(machineId);
        return new CurrentStateAuthorityBinding(
            machine,
            new ObservationStreamId(machine, streamKey),
            mappingProcessor,
            stateProcessor);
    }

    private static MachineStateActivityAuthorityPublication CreateInitialStatePublication(
        CurrentStateAuthorityBinding binding,
        ObservationPosition position,
        MachineState state)
    {
        var projection = new MachineStateActivityProjection(
            binding.StateProcessorId,
            binding.ObservationStreamId,
            position,
            signals: [],
            state,
            activeState: null,
            activeStartedAt: null);
        var identity = new EvaluationAuthorityReplayIdentity(
            binding.StateProcessorId,
            binding.ObservationStreamId,
            position,
            state,
            lastConsumedInstanceId: 1,
            new CurrentStatePolicyReference("continuity/preserve", "1.0"));

        return new MachineStateActivityAuthorityPublication(
            expectedProjectionPosition: null,
            expectedAuthorityRevision: null,
            projection,
            stateChanges: [],
            activityPeriods: [],
            identity);
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
