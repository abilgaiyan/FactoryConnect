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

    [Fact]
    public async Task CombinedCompositionUsesJointAuthorityForDownstreamActivity()
    {
        var machineId = MachineId.New();
        var streamId = new ObservationStreamId(
            machineId,
            "modbus:line-1");
        var processorId = new ObservationProcessorId(
            "machine-state-activity");
        var configuration = ProductionConfiguration();
        var legacyStore = new InMemoryMachineStateActivityProjectionStore();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFactoryConnectEdgePersistence(configuration);
        services.AddSingleton<IMachineStateActivityProjectionStore>(
            legacyStore);

        services.AddFactoryConnectObservationProcessing(
            configuration,
            streamId);
        services.AddFactoryConnectProductionMetricInputs(
            configuration,
            streamId);

        await using var provider = services.BuildServiceProvider();

        var concreteStore = provider.GetRequiredService<
            InMemoryMachineStateActivityAuthorityStore>();
        var authorityStore = provider.GetRequiredService<
            IMachineStateActivityAuthorityStore>();
        var cursorReader = provider.GetRequiredService<
            IMachineStateActivityCursorReader>();
        var processor = provider.GetRequiredService<
            IMappedMachineObservationProcessor>();
        var activityReader = provider.GetRequiredService<
            IProductionContextActivityReader>();
        var resolvedLegacyStore = provider.GetRequiredService<
            IMachineStateActivityProjectionStore>();

        Assert.Same(concreteStore, authorityStore);
        Assert.IsType<JointMachineStateActivityCursorReader>(cursorReader);
        Assert.IsType<JointAuthorityMachineStateActivityProcessor>(processor);
        Assert.IsType<JointProductionContextActivityReader>(activityReader);
        Assert.Same(legacyStore, resolvedLegacyStore);

        var authoritativePosition = new ObservationPosition(1);
        var authoritativePeriod = new DurableMachineActivityPeriod(
            processorId,
            authoritativePosition,
            streamId,
            instanceId: 1,
            sequence: 1,
            new MachineActivityPeriod(
                machineId,
                MachineState.Running,
                new DateTimeOffset(
                    2026,
                    9,
                    14,
                    10,
                    0,
                    0,
                    TimeSpan.Zero),
                new DateTimeOffset(
                    2026,
                    9,
                    14,
                    11,
                    0,
                    0,
                    TimeSpan.Zero)));

        var authoritativeProjection =
            new MachineStateActivityProjection(
                processorId,
                streamId,
                authoritativePosition,
                [],
                MachineState.Running,
                null,
                null);

        var publication =
            new MachineStateActivityAuthorityPublication(
                expectedProjectionPosition: null,
                expectedAuthorityRevision: null,
                authoritativeProjection,
                stateChanges: [],
                activityPeriods: [authoritativePeriod],
                new EvaluationAuthorityReplayIdentity(
                    processorId,
                    streamId,
                    authoritativePosition,
                    MachineState.Running,
                    lastConsumedInstanceId: 1,
                    CanonicalCurrentStateContinuityPolicies
                        .Preserve
                        .Reference));

        Assert.IsType<
            MachineStateActivityAuthorityPublicationAccepted>(
            await authorityStore.PublishAsync(
                publication,
                CancellationToken.None));

        var legacyPosition = new ObservationPosition(99);
        var legacyPeriod = new DurableMachineActivityPeriod(
            processorId,
            legacyPosition,
            streamId,
            instanceId: 99,
            sequence: 99,
            new MachineActivityPeriod(
                machineId,
                MachineState.Faulted,
                new DateTimeOffset(
                    2026,
                    9,
                    14,
                    12,
                    0,
                    0,
                    TimeSpan.Zero),
                new DateTimeOffset(
                    2026,
                    9,
                    14,
                    13,
                    0,
                    0,
                    TimeSpan.Zero)));

        var legacyProjection =
            new MachineStateActivityProjection(
                processorId,
                streamId,
                legacyPosition,
                [],
                MachineState.Faulted,
                null,
                null);

        await legacyStore.CommitAsync(
            new MachineStateActivityProjectionCommit(
                expectedProjection: null,
                legacyProjection,
                stateChanges: [],
                activityPeriods: [legacyPeriod]),
            CancellationToken.None);

        Assert.Equal(
            authoritativePosition,
            await cursorReader.ReadAsync(
                processorId,
                streamId,
                CancellationToken.None));

        var downstreamActivity = await activityReader.ReadAsync(
            streamId,
            afterPosition: null,
            batchSize: 100,
            CancellationToken.None);

        var downstreamPeriod = Assert.Single(downstreamActivity);
        Assert.Equal(authoritativePeriod, downstreamPeriod);

        Assert.DoesNotContain(
            downstreamActivity,
            item => item.Position == legacyPosition);

        Assert.Equal(
            legacyPeriod,
            Assert.Single(
                legacyStore.ReadActivityPeriods(
                    processorId,
                    streamId)));
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

    private static IConfiguration ProductionConfiguration() =>
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
                    ["ProductionProcessing:BatchSize"] = "100",
                    ["ProductionProcessing:PollingInterval"] = "00:00:01",
                    ["ProductionProcessing:CompanyId"] = "COMP-1",
                    ["ProductionProcessing:SiteId"] = "SITE-1",
                    ["ProductionProcessing:ProductionLineId"] = "LINE-1",
                    ["ProductionProcessing:ContextAssignmentId"] = "CTX-1",
                    ["ProductionProcessing:ContextEffectiveFromUtc"] =
                        "2026-01-01T00:00:00+00:00",
                    ["ProductionProcessing:QuantityStreamKey"] =
                        "production-quantity",
                    ["ProductionProcessing:Shift:AssignmentId"] =
                        "SHIFT-SCHEDULE-1",
                    ["ProductionProcessing:Shift:ShiftId"] = "SHIFT-1",
                    ["ProductionProcessing:Shift:Name"] = "Shift 1",
                    ["ProductionProcessing:Shift:TimeZoneId"] = "UTC",
                    ["ProductionProcessing:Shift:StartsAtLocal"] =
                        "06:00:00",
                    ["ProductionProcessing:Shift:EndsAtLocal"] =
                        "14:00:00",
                    ["ProductionProcessing:Shift:EffectiveFrom"] =
                        "2026-01-01",
                    ["ProductionProcessing:PlannedProduction:AssignmentId"] =
                        "POT-1",
                    ["ProductionProcessing:PlannedProduction:TimeZoneId"] =
                        "UTC",
                    ["ProductionProcessing:PlannedProduction:StartsAtLocal"] =
                        "06:00:00",
                    ["ProductionProcessing:PlannedProduction:EndsAtLocal"] =
                        "14:00:00",
                    ["ProductionProcessing:PlannedProduction:EffectiveFrom"] =
                        "2026-01-01",
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
