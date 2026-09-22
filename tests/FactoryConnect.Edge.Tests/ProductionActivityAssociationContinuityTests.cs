using FactoryConnect.Abstractions;
using FactoryConnect.Edge;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class ProductionActivityAssociationContinuityTests
{
    [Fact]
    public async Task CombinedFinalizedInMemoryCarriesActivityIntoProductionReader()
    {
        await AssertCombinedCompositionCarriesActivityAsync(
            Configuration("InMemory"));
    }

    [Fact]
    public async Task CombinedSqlFallbackCarriesActivityIntoProductionReader()
    {
        await AssertCombinedCompositionCarriesActivityAsync(
            Configuration(
                "SqlServer",
                "Server=test;Database=FactoryConnect;Integrated Security=True;TrustServerCertificate=True"));
    }

    private static async Task AssertCombinedCompositionCarriesActivityAsync(
        IConfiguration configuration)
    {
        var machineId = MachineId.New();
        var streamId = new ObservationStreamId(machineId, "modbus:line-1");
        var processorId = new ObservationProcessorId("machine-state-activity");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFactoryConnectEdgePersistence(configuration);
        services.AddFactoryConnectObservationProcessing(configuration, streamId);
        services.AddFactoryConnectProductionMetricInputs(configuration, streamId);

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

    private static IConfiguration Configuration(
        string provider,
        string? connectionString = null)
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
                    ["ProductionProcessing:ShiftSchedules:0:ActiveDays:0"] = "Monday",
                    ["ProductionProcessing:ShiftSchedules:0:ActiveDays:1"] = "Tuesday",
                    ["ProductionProcessing:ShiftSchedules:0:ActiveDays:2"] = "Wednesday",
                    ["ProductionProcessing:ShiftSchedules:0:ActiveDays:3"] = "Thursday",
                    ["ProductionProcessing:ShiftSchedules:0:ActiveDays:4"] = "Friday",
                    ["ProductionProcessing:ShiftSchedules:0:ActiveDays:5"] = "Saturday",
                    ["ProductionProcessing:ShiftSchedules:0:ActiveDays:6"] = "Sunday",
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
        };

        if (connectionString is not null)
        {
            values["PersistenceProviders:SqlServer:ConnectionString"] = connectionString;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
