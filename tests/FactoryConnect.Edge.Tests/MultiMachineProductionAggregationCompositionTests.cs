using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Core.Machines;
using FactoryConnect.Edge;
using FactoryConnect.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class MultiMachineProductionAggregationCompositionTests
{
    [Fact]
    public async Task TwoMachinesPublishAndAggregateIndependentlyAcrossOvernightShift()
    {
        var machineA = new MachineId(Guid.NewGuid());
        var machineB = new MachineId(Guid.NewGuid());
        var activityA = new ObservationStreamId(machineA, "activity-a");
        var activityB = new ObservationStreamId(machineB, "activity-b");
        ObservationStreamId[] activityStreams = [activityA, activityB];
        var configuration = CreateConfiguration(machineA, machineB);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFactoryConnectEdgePersistence(configuration);
        services.AddFactoryConnectObservationProcessing(
            configuration,
            activityStreams);
        services.AddFactoryConnectProductionMetricInputs(
            configuration,
            activityStreams);
        services.AddFactoryConnectMetricAggregation(
            configuration,
            [machineA, machineB]);

        await using var provider = services.BuildServiceProvider();
        var authorityStore = provider.GetRequiredService<
            InMemoryMachineStateActivityAuthorityStore>();
        Assert.Same(
            authorityStore,
            provider.GetRequiredService<IMachineStateActivityAuthorityStore>());
        Assert.Null(provider.GetService<
            MachineShiftOccurrenceRosterMaterializationRuntimeSet>());
        await SeedActivityAsync(authorityStore, activityA, 1);
        await SeedActivityAsync(authorityStore, activityB, 1);

        var quantityReader = provider.GetRequiredService<
            InMemoryProductionQuantityEvidenceReader>();
        quantityReader.Add(CreateQuantity(machineA, "quantity-a", "QA", "LINE-A", "CTX-A"));
        quantityReader.Add(CreateQuantity(machineB, "quantity-b", "QB", "LINE-B", "CTX-B"));

        var producers = provider.GetRequiredService<ProductionMetricInputRuntimeSet>();
        Assert.Equal(2, producers.ActivityRuntimes.Count);
        Assert.Equal(2, producers.QuantityRuntimes.Count);

        foreach (var runtime in producers.ActivityRuntimes)
        {
            Assert.Equal(1, await runtime.RunCycleAsync(CancellationToken.None));
        }

        foreach (var runtime in producers.QuantityRuntimes)
        {
            Assert.Equal(1, await runtime.RunCycleAsync(CancellationToken.None));
        }

        var inputReader = provider.GetRequiredService<IMetricInputReader>();
        var batchA = await ReadAllAsync(inputReader, machineA);
        var batchB = await ReadAllAsync(inputReader, machineB);

        Assert.Equal(4, batchA.Facts.Count);
        Assert.Equal(4, batchB.Facts.Count);
        Assert.All(batchA.Facts, fact => Assert.Equal(machineA, fact.Fact.MachineId));
        Assert.All(batchB.Facts, fact => Assert.Equal(machineB, fact.Fact.MachineId));
        Assert.DoesNotContain(batchA.Facts, fact => fact.Fact.MachineId == machineB);
        Assert.DoesNotContain(batchB.Facts, fact => fact.Fact.MachineId == machineA);
        Assert.All(
            batchA.Facts,
            fact => Assert.Equal(
                new DateOnly(2026, 8, 27),
                fact.ProductionDayId.BusinessDate));
        Assert.All(
            batchB.Facts,
            fact => Assert.Equal(
                new DateOnly(2026, 8, 27),
                fact.ProductionDayId.BusinessDate));

        var authorityPeriodsA = authorityStore.ReadActivityPeriods(
            new ObservationProcessorId("machine-state-activity"),
            activityA);
        var authorityPeriodsB = authorityStore.ReadActivityPeriods(
            new ObservationProcessorId("machine-state-activity"),
            activityB);
        Assert.Single(authorityPeriodsA);
        Assert.Single(authorityPeriodsB);
        Assert.All(authorityPeriodsA, period => Assert.Equal(activityA, period.StreamId));
        Assert.All(authorityPeriodsB, period => Assert.Equal(activityB, period.StreamId));
        Assert.DoesNotContain(authorityPeriodsA, period => period.StreamId == activityB);
        Assert.DoesNotContain(authorityPeriodsB, period => period.StreamId == activityA);

        var aggregationSet = provider.GetRequiredService<
            MetricAggregationProcessingRuntimeSet>();
        Assert.True(await aggregationSet.RunCycleAsync(CancellationToken.None));

        var aggregationStore = provider.GetRequiredService<IMetricAggregationStore>();
        var runtimeA = FindAggregationRuntime(aggregationSet, machineA);
        var runtimeB = FindAggregationRuntime(aggregationSet, machineB);
        var streamA = MetricInputStreamId.ForMachine(machineA);
        var streamB = MetricInputStreamId.ForMachine(machineB);
        var checkpointA = await aggregationStore.ReadCheckpointAsync(
            runtimeA.ProcessorId,
            streamA,
            CancellationToken.None);
        var checkpointB = await aggregationStore.ReadCheckpointAsync(
            runtimeB.ProcessorId,
            streamB,
            CancellationToken.None);

        Assert.NotNull(checkpointA);
        Assert.NotNull(checkpointB);
        Assert.Equal(batchA.ThroughPosition, checkpointA.Position);
        Assert.Equal(batchB.ThroughPosition, checkpointB.Position);

        var runningA = Assert.Single(
            batchA.Facts,
            static item => item.Fact.Key == "duration.running");
        var dayAggregateA = await aggregationStore.ReadProductionDayAggregateAsync(
            runtimeA.ProcessorId,
            new ProductionDayMetricAggregateKey(
                machineA,
                runningA.ProductionDayId,
                runningA.Fact.Key),
            CancellationToken.None);
        Assert.NotNull(dayAggregateA);
        Assert.Equal(1800m, dayAggregateA.Value);

        var runningB = Assert.Single(
            batchB.Facts,
            static item => item.Fact.Key == "duration.running");
        var dayAggregateB = await aggregationStore.ReadProductionDayAggregateAsync(
            runtimeB.ProcessorId,
            new ProductionDayMetricAggregateKey(
                machineB,
                runningB.ProductionDayId,
                runningB.Fact.Key),
            CancellationToken.None);
        Assert.NotNull(dayAggregateB);
        Assert.Equal(1800m, dayAggregateB.Value);

        var crossMachineA = await aggregationStore.ReadProductionDayAggregateAsync(
            runtimeA.ProcessorId,
            new ProductionDayMetricAggregateKey(
                machineB,
                batchB.Facts[0].ProductionDayId,
                batchB.Facts[0].Fact.Key),
            CancellationToken.None);
        var crossMachineB = await aggregationStore.ReadProductionDayAggregateAsync(
            runtimeB.ProcessorId,
            new ProductionDayMetricAggregateKey(
                machineA,
                batchA.Facts[0].ProductionDayId,
                batchA.Facts[0].Fact.Key),
            CancellationToken.None);
        Assert.Null(crossMachineA);
        Assert.Null(crossMachineB);
    }

    private static MetricAggregationProcessingRuntime FindAggregationRuntime(
        MetricAggregationProcessingRuntimeSet runtimes,
        MachineId machineId) =>
        runtimes.Runtimes.Single(runtime =>
            runtime.ProcessorId.Value.EndsWith(
                machineId.Value.ToString("D"),
                StringComparison.Ordinal));

    private static async Task<MetricInputReadBatch> ReadAllAsync(
        IMetricInputReader reader,
        MachineId machineId) =>
        await reader.ReadAsync(
            new MetricInputReadRequest(
                MetricInputStreamId.ForMachine(machineId),
                afterPosition: null,
                maxCount: 100),
            CancellationToken.None);

    private static DurableProductionQuantityEvidence CreateQuantity(
        MachineId machineId,
        string streamKey,
        string evidenceId,
        string lineId,
        string contextId) =>
        new(
            new ObservationPosition(1),
            new ObservationStreamId(machineId, streamKey),
            new ProductionQuantityEvidence
            {
                Id = new ProductionQuantityEvidenceId(evidenceId),
                CompanyId = new CompanyId("COMP-1"),
                SiteId = new SiteId("SITE-1"),
                ProductionLineId = new ProductionLineId(lineId),
                MachineId = machineId,
                ShiftId = new ShiftId("NIGHT"),
                ProductionContextAssignmentId =
                    new ProductionContextAssignmentId(contextId),
                OccurredAtUtc = new DateTimeOffset(
                    2026,
                    8,
                    28,
                    1,
                    30,
                    0,
                    TimeSpan.Zero),
                GoodQuantity = 2,
            });

    private static async Task SeedActivityAsync(
        InMemoryMachineStateActivityAuthorityStore store,
        ObservationStreamId streamId,
        ulong positionValue)
    {
        var processorId = new ObservationProcessorId("machine-state-activity");
        var position = new ObservationPosition(positionValue);
        var period = new DurableMachineActivityPeriod(
            processorId,
            position,
            streamId,
            instanceId: 1,
            sequence: positionValue,
            new MachineActivityPeriod(
                streamId.MachineId,
                MachineState.Running,
                new DateTimeOffset(2026, 8, 28, 0, 30, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 28, 1, 0, 0, TimeSpan.Zero)));
        var projection = new MachineStateActivityProjection(
            processorId,
            streamId,
            position,
            [],
            MachineState.Running,
            activeState: null,
            activeStartedAt: null);
        var publication = new MachineStateActivityAuthorityPublication(
            expectedProjectionPosition: null,
            expectedAuthorityRevision: null,
            projection,
            stateChanges: [],
            activityPeriods: [period],
            new EvaluationAuthorityReplayIdentity(
                processorId,
                streamId,
                position,
                MachineState.Running,
                lastConsumedInstanceId: 1,
                CanonicalCurrentStateContinuityPolicies.Preserve.Reference));

        Assert.IsType<MachineStateActivityAuthorityPublicationAccepted>(
            await store.PublishAsync(publication, CancellationToken.None));
    }

    private static IConfiguration CreateConfiguration(
        MachineId machineA,
        MachineId machineB)
    {
        var values = new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "InMemory",
            ["ObservationProcessing:BatchSize"] = "100",
            ["ObservationProcessing:PollingInterval"] = "00:00:01",
            ["ProductionProcessing:BatchSize"] = "100",
            ["ProductionProcessing:PollingInterval"] = "00:00:01",
            ["MetricAggregation:BatchSize"] = "100",
            ["MetricAggregation:PollingInterval"] = "00:00:01",
        };
        AddObservationStream(values, 0, machineA, "activity-a", "DI1");
        AddObservationStream(values, 1, machineB, "activity-b", "DI2");
        AddShift(values, 0, "LINE-A", "SHIFT-A");
        AddShift(values, 1, "LINE-B", "SHIFT-B");
        AddMachine(values, 0, machineA, "activity-a", "quantity-a", "LINE-A", "CTX-A", "POT-A");
        AddMachine(values, 1, machineB, "activity-b", "quantity-b", "LINE-B", "CTX-B", "POT-B");

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private static void AddObservationStream(
        Dictionary<string, string?> values,
        int index,
        MachineId machineId,
        string streamKey,
        string address)
    {
        var prefix = $"ObservationProcessing:Streams:{index}";
        values[$"{prefix}:MachineId"] = machineId.ToString();
        values[$"{prefix}:StreamKey"] = streamKey;
        values[$"{prefix}:Mappings:0:Source"] = "modbus";
        values[$"{prefix}:Mappings:0:Address"] = address;
        values[$"{prefix}:Mappings:0:SignalKey"] = CanonicalSignalKeys.Running;
        values[$"{prefix}:Mappings:0:Type"] = "Digital";
    }

    private static void AddShift(
        Dictionary<string, string?> values,
        int index,
        string lineId,
        string assignmentId)
    {
        var prefix = $"ProductionProcessing:ShiftSchedules:{index}";
        values[$"{prefix}:AssignmentId"] = assignmentId;
        values[$"{prefix}:CompanyId"] = "COMP-1";
        values[$"{prefix}:SiteId"] = "SITE-1";
        values[$"{prefix}:ProductionLineId"] = lineId;
        values[$"{prefix}:ShiftId"] = "NIGHT";
        values[$"{prefix}:Name"] = "Night Shift";
        values[$"{prefix}:TimeZoneId"] = "UTC";
        values[$"{prefix}:StartsAtLocal"] = "18:30:00";
        values[$"{prefix}:EndsAtLocal"] = "02:30:00";
        values[$"{prefix}:ActiveDays:0"] = "Monday";
        values[$"{prefix}:ActiveDays:1"] = "Tuesday";
        values[$"{prefix}:ActiveDays:2"] = "Wednesday";
        values[$"{prefix}:ActiveDays:3"] = "Thursday";
        values[$"{prefix}:ActiveDays:4"] = "Friday";
        values[$"{prefix}:ActiveDays:5"] = "Saturday";
        values[$"{prefix}:ActiveDays:6"] = "Sunday";
        values[$"{prefix}:EffectiveFrom"] = "2026-01-01";
    }

    private static void AddMachine(
        Dictionary<string, string?> values,
        int index,
        MachineId machineId,
        string activityStreamKey,
        string quantityStreamKey,
        string lineId,
        string contextId,
        string plannedAssignmentId)
    {
        var prefix = $"ProductionProcessing:Machines:{index}";
        values[$"{prefix}:MachineId"] = machineId.Value.ToString("D");
        values[$"{prefix}:ActivityStreamKey"] = activityStreamKey;
        values[$"{prefix}:QuantityStreamKey"] = quantityStreamKey;
        values[$"{prefix}:CompanyId"] = "COMP-1";
        values[$"{prefix}:SiteId"] = "SITE-1";
        values[$"{prefix}:ProductionLineId"] = lineId;
        values[$"{prefix}:Contexts:0:AssignmentId"] = contextId;
        values[$"{prefix}:Contexts:0:EffectiveFromUtc"] = "2026-01-01T00:00:00+00:00";
        values[$"{prefix}:PlannedProduction:AssignmentId"] = plannedAssignmentId;
        values[$"{prefix}:PlannedProduction:TimeZoneId"] = "UTC";
        values[$"{prefix}:PlannedProduction:StartsAtLocal"] = "18:30:00";
        values[$"{prefix}:PlannedProduction:EndsAtLocal"] = "02:30:00";
        values[$"{prefix}:PlannedProduction:EffectiveFrom"] = "2026-01-01";
    }
}
