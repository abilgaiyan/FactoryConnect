using FactoryConnect.Abstractions;
using FactoryConnect.Core.Machines;
using FactoryConnect.Edge;
using FactoryConnect.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class JointAuthorityCompositionOwnershipTests
{
    [Fact]
    public void ObservationCompositionReplacesCompetingJointBindings()
    {
        var streamId = new ObservationStreamId(
            MachineId.New(),
            "modbus:line-1");
        var configuration = Configuration();
        var competingStore = new InMemoryMachineStateActivityAuthorityStore();
        var competingProcessor = new CompetingMappedProcessor();
        var competingPolicy = new CurrentStateContinuityPolicy(
            new CurrentStatePolicyReference("continuity/reset", "1.0"),
            StateContinuityMode.Reset);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMachineStateActivityAuthorityStore>(competingStore);
        services.AddSingleton<IMappedMachineObservationProcessor>(competingProcessor);
        services.AddSingleton(competingPolicy);
        services.AddFactoryConnectEdgePersistence(configuration);

        services.AddFactoryConnectObservationProcessing(
            configuration,
            streamId);

        using var provider = services.BuildServiceProvider();
        var concreteStore = provider.GetRequiredService<
            InMemoryMachineStateActivityAuthorityStore>();
        var authorityStore = provider.GetRequiredService<
            IMachineStateActivityAuthorityStore>();
        var processor = provider.GetRequiredService<
            IMappedMachineObservationProcessor>();
        var policy = provider.GetRequiredService<
            CurrentStateContinuityPolicy>();

        Assert.Same(concreteStore, authorityStore);
        Assert.NotSame(competingStore, authorityStore);
        Assert.IsType<JointMachineStateActivityCursorReader>(
            provider.GetRequiredService<IMachineStateActivityCursorReader>());
        Assert.IsType<JointAuthorityMachineStateActivityProcessor>(processor);
        Assert.NotSame(competingProcessor, processor);
        Assert.Same(CanonicalCurrentStateContinuityPolicies.Preserve, policy);
        Assert.Equal("continuity/preserve", policy.Reference.Identity);
        Assert.Equal("1.0", policy.Reference.Version);
        Assert.Equal(StateContinuityMode.Preserve, policy.Mode);
    }

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Persistence:Provider"] = "InMemory",
                    ["ObservationProcessing:BatchSize"] = "10",
                    ["ObservationProcessing:PollingInterval"] = "00:00:01",
                    ["ObservationProcessing:Mappings:0:Source"] = "modbus",
                    ["ObservationProcessing:Mappings:0:Address"] = "DI1",
                    ["ObservationProcessing:Mappings:0:SignalKey"] =
                        CanonicalSignalKeys.Running,
                    ["ObservationProcessing:Mappings:0:Type"] = "Digital",
                    ["ObservationProcessing:Mappings:0:Invert"] = "false",
                })
            .Build();

    private sealed class CompetingMappedProcessor :
        IMappedMachineObservationProcessor
    {
        public ObservationProcessorId ProcessorId { get; } =
            new("competing");

        public ValueTask ProcessAsync(
            IReadOnlyList<DurableMappedMachineObservation> observations,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
