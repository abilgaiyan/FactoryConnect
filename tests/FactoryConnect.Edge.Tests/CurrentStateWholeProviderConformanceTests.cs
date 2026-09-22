using FactoryConnect.Abstractions;
using FactoryConnect.Core.Machines;
using FactoryConnect.Edge;
using FactoryConnect.Persistence;
using FactoryConnect.Protocols.MTConnect;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class CurrentStateWholeProviderConformanceTests
{
    [Fact]
    public async Task FinalizedInMemoryCarriesOneAuthorityGraphThroughPublicCurrentStateReader()
    {
        var machineId = MachineId.New();
        var inventory = Inventory(machineId);
        var streamId = inventory.ActivityStreams[0];
        var mappingProcessorId = new ObservationProcessorId("canonical-mapping");
        var stateProcessorId = new ObservationProcessorId("machine-state-activity");
        var readAsOf = DateTimeOffset.UnixEpoch.AddMinutes(2).AddSeconds(30);
        var configuration = Configuration();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(readAsOf));
        services.AddFactoryConnectEdgePersistence(
            configuration,
            PersistenceProviderCapabilities.CurrentStateAuthorityReading);
        services.AddFactoryConnectObservationProcessing(configuration, streamId);
        services.AddFactoryConnectCurrentMachineState(configuration, inventory);
        services.AddFactoryConnectProductionMetricInputs(configuration, streamId);

        using var provider = services.BuildServiceProvider();

        var observationStore = provider.GetRequiredService<IObservationIngestionStore>();
        var mappingStore = provider.GetRequiredService<IMappingCoverageAuthorityStore>();
        var stateStore = provider.GetRequiredService<IMachineStateActivityAuthorityStore>();
        var cutProvider = provider.GetRequiredService<ICurrentStateAuthorityCutProvider>();
        var activityReader = provider.GetRequiredService<IProductionContextActivityReader>();
        var currentStateReader = provider.GetRequiredService<ICurrentMachineStateReader>();

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
                CanonicalCurrentStateContinuityPolicies.Preserve.Reference));

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

        var currentStateResult = await currentStateReader.ReadAsync(
            machineId,
            CancellationToken.None);
        var evidence = Assert.IsType<CurrentMachineStateEvidence>(currentStateResult);

        Assert.Equal(machineId, evidence.MachineId);
        Assert.Equal(MachineState.Idle, evidence.MachineState);
        Assert.Equal(CurrentStateCoverage.Complete, evidence.Coverage);
        Assert.Equal(CurrentStateFreshness.Current, evidence.Freshness);
        Assert.Equal(CurrentStateUsability.Current, evidence.Usability);
        Assert.Equal(readAsOf, evidence.ReadAsOf);

        var carried = await activityReader.ReadAsync(
            streamId,
            afterPosition: null,
            batchSize: 10,
            CancellationToken.None);

        Assert.Equal(activity, Assert.Single(carried));
    }

    [Fact]
    public async Task PublicCurrentStateReaderPreservesNoEvidenceFromStableEmptyAuthorityCut()
    {
        var machineId = MachineId.New();
        var inventory = Inventory(machineId);
        var configuration = Configuration();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(
            new FixedTimeProvider(DateTimeOffset.UnixEpoch.AddMinutes(5)));
        services.AddFactoryConnectEdgePersistence(
            configuration,
            PersistenceProviderCapabilities.CurrentStateAuthorityReading);
        services.AddFactoryConnectObservationProcessing(
            configuration,
            inventory.ActivityStreams);
        services.AddFactoryConnectCurrentMachineState(configuration, inventory);

        using var provider = services.BuildServiceProvider();
        var reader = provider.GetRequiredService<ICurrentMachineStateReader>();

        var result = await reader.ReadAsync(machineId, CancellationToken.None);
        var noEvidence = Assert.IsType<CurrentMachineStateNoEvidence>(result);

        Assert.Equal(machineId, noEvidence.MachineId);
        Assert.Equal(CurrentStateCoverage.Indeterminate, noEvidence.Coverage);
    }

    private static MtConnectMachineInventory Inventory(MachineId machineId) =>
        new(
            [
                new MtConnectAcquisitionOptions(
                    new MtConnectEndpoint(new Uri("http://localhost:5001/")),
                    machineId,
                    "CNC-01",
                    fromSequence: 1,
                    pollingInterval: TimeSpan.FromSeconds(1)),
            ]);

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
                    ["CurrentState:Freshness:MaximumCurrentAge"] = "00:01:00",
                    ["ProductionProcessing:BatchSize"] = "100",
                    ["ProductionProcessing:PollingInterval"] = "00:00:01",
                    ["ProductionProcessing:CompanyId"] = "COMP-1",
                    ["ProductionProcessing:SiteId"] = "SITE-1",
                    ["ProductionProcessing:ProductionLineId"] = "LINE-1",
                    ["ProductionProcessing:Contexts:0:AssignmentId"] = "CTX-1",
                    ["ProductionProcessing:Contexts:0:EffectiveFrom"] = "2026-01-01T00:00:00+00:00",
                    ["ProductionProcessing:QuantityStreamKey"] = "production-quantity",
                    ["ProductionProcessing:ShiftSchedules:0:CompanyId"] = "COMP-1",
                    ["ProductionProcessing:ShiftSchedules:0:SiteId"] = "SITE-1",
                    ["ProductionProcessing:ShiftSchedules:0:ProductionLineId"] = "LINE-1",
                    ["ProductionProcessing:ShiftSchedules:0:ActiveDays:0"] = "Sunday",
                    ["ProductionProcessing:ShiftSchedules:0:ActiveDays:1"] = "Monday",
                    ["ProductionProcessing:ShiftSchedules:0:ActiveDays:2"] = "Tuesday",
                    ["ProductionProcessing:ShiftSchedules:0:ActiveDays:3"] = "Wednesday",
                    ["ProductionProcessing:ShiftSchedules:0:ActiveDays:4"] = "Thursday",
                    ["ProductionProcessing:ShiftSchedules:0:ActiveDays:5"] = "Friday",
                    ["ProductionProcessing:ShiftSchedules:0:ActiveDays:6"] = "Saturday",
                    ["ProductionProcessing:ShiftSchedules:0:AssignmentId"] = "SHIFT-SCHEDULE-1",
                    ["ProductionProcessing:ShiftSchedules:0:ShiftId"] = "SHIFT-1",
                    ["ProductionProcessing:ShiftSchedules:0:Name"] = "Shift 1",
                    ["ProductionProcessing:ShiftSchedules:0:TimeZoneId"] = "UTC",
                    ["ProductionProcessing:ShiftSchedules:0:StartsAtLocal"] = "06:00:00",
                    ["ProductionProcessing:ShiftSchedules:0:EndsAtLocal"] = "14:00:00",
                    ["ProductionProcessing:ShiftSchedules:0:EffectiveFrom"] = "2026-01-01",
                    ["ProductionProcessing:PlannedProduction:AssignmentId"] = "POT-1",
                    ["ProductionProcessing:PlannedProduction:TimeZoneId"] = "UTC",
                    ["ProductionProcessing:PlannedProduction:StartsAtLocal"] = "06:00:00",
                    ["ProductionProcessing:PlannedProduction:EndsAtLocal"] = "14:00:00",
                    ["ProductionProcessing:PlannedProduction:EffectiveFrom"] = "2026-01-01",
                })
            .Build();

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
