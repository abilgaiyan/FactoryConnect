using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Edge;
using FactoryConnect.Infrastructure;
using FactoryConnect.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class EdgeObservationAuthorityHandoffTests
{
    [Fact]
    public async Task FinalizedInMemoryUsesProviderOwnedPairAndPublishesIntoActivatedCutProvider()
    {
        var machineId = MachineId.New();
        var streamId = new ObservationStreamId(machineId, "modbus:line-1");
        var configuration = Configuration("InMemory");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFactoryConnectEdgePersistence(
            configuration,
            PersistenceProviderCapabilities.Core |
            PersistenceProviderCapabilities.CurrentStateAuthorityReading);
        services.AddFactoryConnectObservationProcessing(configuration, streamId);

        using var provider = services.BuildServiceProvider();
        var providerServices = provider.GetRequiredService<PersistenceProviderServices>();
        var mapping = provider.GetRequiredService<IMappingCoverageAuthorityStore>();
        var state = provider.GetRequiredService<IMachineStateActivityAuthorityStore>();

        Assert.Same(providerServices.MappingCoverageAuthorityStore, mapping);
        Assert.Same(providerServices.MachineStateActivityAuthorityStore, state);
        Assert.Same(mapping, provider.GetRequiredService<InMemoryMappingCoverageAuthorityStore>());
        Assert.Same(state, provider.GetRequiredService<InMemoryMachineStateActivityAuthorityStore>());

        var rawStore = provider.GetRequiredService<IObservationIngestionStore>();
        await rawStore.CommitAsync(Batch(streamId));
        var pipeline = provider.GetRequiredService<DurableObservationProcessingPipeline>();
        Assert.True(await pipeline.RunCycleAsync());

        var cutProvider = provider.GetRequiredService<ICurrentStateAuthorityCutProvider>();
        Assert.Same(providerServices.CurrentStateAuthorityCutProvider, cutProvider);
        var result = await cutProvider.ReadAuthorityCutAsync(
            new CurrentStateAuthorityBinding(
                machineId,
                streamId,
                new ObservationProcessorId("canonical-mapping"),
                new ObservationProcessorId("machine-state-activity")),
            CancellationToken.None);

        var stable = Assert.IsType<StableCurrentStateAuthorityCut>(result);
        Assert.NotNull(stable.Cut.AcquisitionContact);
        Assert.NotNull(stable.Cut.MappingCoverage);
        Assert.NotNull(stable.Cut.Evaluation);
    }

    [Fact]
    public void SqlUsesOneEdgeFallbackPairAndPreservesConcreteAliases()
    {
        var streamId = new ObservationStreamId(MachineId.New(), "modbus:line-1");
        var configuration = Configuration(
            "SqlServer",
            "Server=test;Database=FactoryConnect;Integrated Security=True;TrustServerCertificate=True");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFactoryConnectEdgePersistence(configuration);
        services.AddFactoryConnectObservationProcessing(configuration, streamId);

        using var provider = services.BuildServiceProvider();
        var providerServices = provider.GetRequiredService<PersistenceProviderServices>();
        Assert.Null(providerServices.MappingCoverageAuthorityStore);
        Assert.Null(providerServices.MachineStateActivityAuthorityStore);

        Assert.Same(
            provider.GetRequiredService<IMappingCoverageAuthorityStore>(),
            provider.GetRequiredService<InMemoryMappingCoverageAuthorityStore>());
        Assert.Same(
            provider.GetRequiredService<IMachineStateActivityAuthorityStore>(),
            provider.GetRequiredService<InMemoryMachineStateActivityAuthorityStore>());
    }

    [Fact]
    public void StandaloneCompositionExposesOneFallbackAuthorityPair()
    {
        var streamId = new ObservationStreamId(MachineId.New(), "modbus:line-1");
        var services = new ServiceCollection();
        services.AddFactoryConnectObservationProcessing(Configuration("InMemory"), streamId);

        using var provider = services.BuildServiceProvider();
        Assert.Same(
            provider.GetRequiredService<IMappingCoverageAuthorityStore>(),
            provider.GetRequiredService<InMemoryMappingCoverageAuthorityStore>());
        Assert.Same(
            provider.GetRequiredService<IMachineStateActivityAuthorityStore>(),
            provider.GetRequiredService<InMemoryMachineStateActivityAuthorityStore>());
        Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IDurableObservationReader>());
    }

    [Fact]
    public void CompetingAuthorityRegistrationsCannotDisplaceFallbackPair()
    {
        var streamId = new ObservationStreamId(MachineId.New(), "modbus:line-1");
        var competingMapping = new InMemoryMappingCoverageAuthorityStore();
        var competingState = new InMemoryMachineStateActivityAuthorityStore();
        var services = new ServiceCollection();
        services.AddSingleton(competingMapping);
        services.AddSingleton<IMappingCoverageAuthorityStore>(competingMapping);
        services.AddSingleton(competingState);
        services.AddSingleton<IMachineStateActivityAuthorityStore>(competingState);
        services.AddFactoryConnectObservationProcessing(Configuration("InMemory"), streamId);

        using var provider = services.BuildServiceProvider();
        var mapping = provider.GetRequiredService<IMappingCoverageAuthorityStore>();
        var state = provider.GetRequiredService<IMachineStateActivityAuthorityStore>();
        Assert.NotSame(competingMapping, mapping);
        Assert.NotSame(competingState, state);
        Assert.Same(mapping, provider.GetRequiredService<InMemoryMappingCoverageAuthorityStore>());
        Assert.Same(state, provider.GetRequiredService<InMemoryMachineStateActivityAuthorityStore>());
    }

    [Fact]
    public void MixedProviderAuthorityRolesFailThroughBothEdgeInterfaces()
    {
        using var source = BuildInMemoryProvider();
        var complete = source.GetRequiredService<PersistenceProviderServices>();
        var mixed = new PersistenceProviderServices(
            complete.ObservationIngestionStore,
            complete.ProductionContextProcessingStore,
            complete.MetricInputReader,
            complete.MetricAggregationStore,
            mappingCoverageAuthorityStore: complete.MappingCoverageAuthorityStore,
            machineStateActivityAuthorityStore: null);

        var services = new ServiceCollection();
        services.AddSingleton(mixed);
        services.AddFactoryConnectObservationProcessing(
            Configuration("InMemory"),
            new ObservationStreamId(MachineId.New(), "modbus:line-1"));

        using var provider = services.BuildServiceProvider();
        var mappingFailure = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IMappingCoverageAuthorityStore>());
        var stateFailure = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IMachineStateActivityAuthorityStore>());
        Assert.Contains("must supply mapping coverage and machine state/activity", mappingFailure.Message);
        Assert.Contains("must supply mapping coverage and machine state/activity", stateFailure.Message);
    }

    private static ServiceProvider BuildInMemoryProvider()
    {
        var services = new ServiceCollection();
        var configuration = Configuration("InMemory");
        services.AddLogging();
        services.AddFactoryConnectEdgePersistence(configuration);
        return services.BuildServiceProvider();
    }

    private static ObservationIngestionBatch Batch(ObservationStreamId streamId) =>
        new(
            null,
            new ObservationCheckpoint(streamId, 1, 2),
            [
                new SequencedMachineObservation(
                    1,
                    new MachineObservation
                    {
                        MachineId = streamId.MachineId,
                        Source = "modbus",
                        Address = "DI1",
                        Type = SignalType.Digital,
                        Value = true,
                        Timestamp = DateTimeOffset.UnixEpoch,
                    }),
            ],
            DateTimeOffset.UnixEpoch);

    private static IConfiguration Configuration(string provider, string? connectionString = null)
    {
        Dictionary<string, string?> values = new()
        {
            ["Persistence:Provider"] = provider,
            ["ObservationProcessing:BatchSize"] = "10",
            ["ObservationProcessing:PollingInterval"] = "00:00:01",
            ["ObservationProcessing:Mappings:0:Source"] = "modbus",
            ["ObservationProcessing:Mappings:0:Address"] = "DI1",
            ["ObservationProcessing:Mappings:0:SignalKey"] = CanonicalSignalKeys.Running,
            ["ObservationProcessing:Mappings:0:Type"] = "Digital",
            ["ObservationProcessing:Mappings:0:Invert"] = "false",
        };

        if (connectionString is not null)
        {
            values["PersistenceProviders:SqlServer:ConnectionString"] = connectionString;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
