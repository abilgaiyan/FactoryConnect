using FactoryConnect.Abstractions;
using FactoryConnect.Edge;
using FactoryConnect.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class CurrentStateWholeProviderConformanceTests
{
    [Fact]
    public async Task FinalizedInMemoryCarriesOneAuthorityGraphAcrossCutAndProductionReader()
    {
        var machineId = MachineId.New();
        var streamId = new ObservationStreamId(machineId, "modbus:line-1");
        var mappingProcessorId = new ObservationProcessorId("canonical-mapping");
        var stateProcessorId = new ObservationProcessorId("machine-state-activity");
        var configuration = Configuration();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFactoryConnectEdgePersistence(
            configuration,
            PersistenceProviderCapabilities.CurrentStateAuthorityReading);
        services.AddFactoryConnectObservationProcessing(configuration, streamId);
        services.AddFactoryConnectProductionMetricInputs(configuration, streamId);

        using var provider = services.BuildServiceProvider();

        var observationStore = provider.GetRequiredService<IObservationIngestionStore>();
        var mappingStore = provider.GetRequiredService<IMappingCoverageAuthorityStore>();
        var stateStore = provider.GetRequiredService<IMachineStateActivityAuthorityStore>();
        var cutProvider = provider.GetRequiredService<ICurrentStateAuthorityCutProvider>();
        var activityReader = provider.GetRequiredService<IProductionContextActivityReader>();

        await observationStore.CommitAsync(
            new ObservationIngestionBatch(
                expectedCheckpoint: null,
                new ObservationCheckpoint(streamId, instanceId: 1, nextSequence: 2),
                [
                    new SequencedMachineObservation(
                        sequence: 1,
                        new MachineObservation
                        {
                            MachineId = machineId,
                            Source = "modbus",
                            Address = "DI1",
                            Type = SignalType.Digital,
                            Value = true,
                            Timestamp = DateTimeOffset.UnixEpoch,
                        }),
                ],
                successfulContactTime: DateTimeOffset.UnixEpoch.AddMinutes(2)));

        var acquisition = await observationStore
            .ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(acquisition);
        Assert.NotNull(acquisition.RawAcceptedThrough);
        var authorityPosition = acquisition.RawAcceptedThrough;

        await mappingStore.CommitAsync(
            new MappingCoverageCommit(
                expectedAuthority: null,
                mappingProcessorId,
                streamId,
                authorityPosition,
                authorityPosition));
        var mapping = await mappingStore.ReadAsync(mappingProcessorId, streamId);
        Assert.NotNull(mapping);

        var activity = new DurableMachineActivityPeriod(
            stateProcessorId,
            authorityPosition,
            streamId,
            instanceId: 1,
            sequence: 1,
            new MachineActivityPeriod(
                machineId,
                MachineState.Running,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddMinutes(1)));
        var projection = new MachineStateActivityProjection(
            stateProcessorId,
            streamId,
            authorityPosition,
            [],
            MachineState.Idle,
            activeState: null,
            activeStartedAt: null);
        var publication = new MachineStateActivityAuthorityPublication(
            expectedProjectionPosition: null,
            expectedAuthorityRevision: null,
            projection,
            stateChanges: [],
            activityPeriods: [activity],
            new EvaluationAuthorityReplayIdentity(
                stateProcessorId,
                streamId,
                authorityPosition,
                projection.State,
                lastConsumedInstanceId: 1,
                new CurrentStatePolicyReference("continuity/preserve", "1.0")));

        var publicationResult = await stateStore.PublishAsync(publication);
        var accepted = Assert.IsType<MachineStateActivityAuthorityPublicationAccepted>(
            publicationResult);

        var binding = new CurrentStateAuthorityBinding(
            machineId,
            streamId,
            mappingProcessorId,
            stateProcessorId);
        var cutResult = await cutProvider.ReadAuthorityCutAsync(
            binding,
            CancellationToken.None);
        var stable = Assert.IsType<StableCurrentStateAuthorityCut>(cutResult);

        Assert.Equal(binding, stable.Cut.Binding);
        Assert.Equal(acquisition, stable.Cut.AcquisitionContact);
        Assert.Equal(mapping, stable.Cut.MappingCoverage);
        Assert.Equal(accepted.Snapshot.EvaluationAuthority, stable.Cut.Evaluation);

        var carried = await activityReader.ReadAsync(
            streamId,
            afterPosition: null,
            batchSize: 10,
            CancellationToken.None);

        Assert.Equal(activity, Assert.Single(carried));
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
                    ["ObservationProcessing:Mappings:0:SignalKey"] = CanonicalSignalKeys.Running,
                    ["ObservationProcessing:Mappings:0:Type"] = "Digital",
                    ["ObservationProcessing:Mappings:0:Invert"] = "false",
                    ["ProductionProcessing:BatchSize"] = "100",
                    ["ProductionProcessing:PollingInterval"] = "00:00:01",
                    ["ProductionProcessing:CompanyId"] = "COMP-1",
                    ["ProductionProcessing:SiteId"] = "SITE-1",
                    ["ProductionProcessing:ProductionLineId"] = "LINE-1",
                    ["ProductionProcessing:ContextAssignmentId"] = "CTX-1",
                    ["ProductionProcessing:ContextEffectiveFromUtc"] = "2026-01-01T00:00:00+00:00",
                    ["ProductionProcessing:QuantityStreamKey"] = "production-quantity",
                    ["ProductionProcessing:Shift:AssignmentId"] = "SHIFT-SCHEDULE-1",
                    ["ProductionProcessing:Shift:ShiftId"] = "SHIFT-1",
                    ["ProductionProcessing:Shift:Name"] = "Shift 1",
                    ["ProductionProcessing:Shift:TimeZoneId"] = "UTC",
                    ["ProductionProcessing:Shift:StartsAtLocal"] = "06:00:00",
                    ["ProductionProcessing:Shift:EndsAtLocal"] = "14:00:00",
                    ["ProductionProcessing:Shift:EffectiveFrom"] = "2026-01-01",
                    ["ProductionProcessing:PlannedProduction:AssignmentId"] = "POT-1",
                    ["ProductionProcessing:PlannedProduction:TimeZoneId"] = "UTC",
                    ["ProductionProcessing:PlannedProduction:StartsAtLocal"] = "06:00:00",
                    ["ProductionProcessing:PlannedProduction:EndsAtLocal"] = "14:00:00",
                    ["ProductionProcessing:PlannedProduction:EffectiveFrom"] = "2026-01-01",
                })
            .Build();
}
