using FactoryConnect.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class ProductionOnlyActivityContinuityTests
{
    [Fact]
    public async Task ProductionOnlyCarriesActivityIntoProductionReader()
    {
        var machineId = MachineId.New();
        var streamId = new ObservationStreamId(machineId, "modbus:line-1");
        var processorId = new ObservationProcessorId("machine-state-activity");
        var services = new ServiceCollection();
        services.AddFactoryConnectProductionMetricInputs(Configuration(), streamId);

        using var provider = services.BuildServiceProvider();

        var activity = new DurableMachineActivityPeriod(
            processorId,
            new ObservationPosition(2),
            streamId,
            instanceId: 1,
            sequence: 1,
            new MachineActivityPeriod(
                machineId,
                MachineState.Running,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddMinutes(1)));
        var projection = new MachineStateActivityProjection(
            processorId,
            streamId,
            new ObservationPosition(2),
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
                processorId,
                streamId,
                projection.Position,
                projection.State,
                lastConsumedInstanceId: 1,
                new CurrentStatePolicyReference("continuity/preserve", "1.0")));

        var authorityStore = provider.GetRequiredService<IMachineStateActivityAuthorityStore>();
        var publicationResult = await authorityStore.PublishAsync(publication);
        Assert.IsType<MachineStateActivityAuthorityPublicationAccepted>(publicationResult);

        var reader = provider.GetRequiredService<IProductionContextActivityReader>();
        var carried = await reader.ReadAsync(
            streamId,
            afterPosition: null,
            batchSize: 10,
            CancellationToken.None);

        var observed = Assert.Single(carried);
        Assert.Equal(activity, observed);
    }

    private static IConfiguration Configuration()
    {
        Dictionary<string, string?> values = new()
        {
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
        };

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
