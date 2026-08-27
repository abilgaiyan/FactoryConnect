using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Edge;
using FactoryConnect.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class ProductionMetricInputAggregationCompositionTests
{
    [Fact]
    public async Task ActivityAndQuantityProducersFeedOneAggregateStreamAndResume()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var activityStreamId = new ObservationStreamId(machineId, "activity");
        var quantityStreamId = new ObservationStreamId(
            machineId,
            "production-quantity");
        var configuration = CreateConfiguration();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFactoryConnectEdgePersistence(configuration);
        services.AddFactoryConnectObservationProcessing(
            configuration,
            activityStreamId);
        services.AddFactoryConnectProductionMetricInputs(
            configuration,
            activityStreamId);
        services.AddFactoryConnectMetricAggregation(
            configuration,
            [machineId]);

        await using var provider = services.BuildServiceProvider();
        var projectionStore = provider.GetRequiredService<
            InMemoryMachineStateActivityProjectionStore>();
        await SeedActivityAsync(projectionStore, activityStreamId);

        var quantityReader = provider.GetRequiredService<
            InMemoryProductionQuantityEvidenceReader>();
        quantityReader.Add(
            new DurableProductionQuantityEvidence(
                new ObservationPosition(1),
                quantityStreamId,
                new ProductionQuantityEvidence
                {
                    Id = new ProductionQuantityEvidenceId("Q-1"),
                    CompanyId = new CompanyId("COMP-1"),
                    SiteId = new SiteId("SITE-1"),
                    ProductionLineId = new ProductionLineId("LINE-1"),
                    MachineId = machineId,
                    ShiftId = new ShiftId("SHIFT-1"),
                    ProductionContextAssignmentId =
                        new ProductionContextAssignmentId("CTX-1"),
                    OccurredAtUtc = new DateTimeOffset(
                        2026,
                        8,
                        27,
                        10,
                        30,
                        0,
                        TimeSpan.Zero),
                    GoodQuantity = 3,
                }));

        var producers = provider.GetRequiredService<
            ProductionMetricInputRuntimeSet>();
        Assert.Equal(
            1,
            await producers.ActivityRuntimes[0].RunCycleAsync(
                CancellationToken.None));
        Assert.Equal(
            1,
            await producers.QuantityRuntimes[0].RunCycleAsync(
                CancellationToken.None));

        var inputReader = provider.GetRequiredService<IMetricInputReader>();
        var metricStreamId = MetricInputStreamId.ForMachine(machineId);
        var inputBatch = await inputReader.ReadAsync(
            new MetricInputReadRequest(
                metricStreamId,
                afterPosition: null,
                maxCount: 100),
            CancellationToken.None);

        Assert.NotEmpty(inputBatch.Facts);
        Assert.Contains(
            inputBatch.Facts,
            static fact => fact.Fact.Key == "running-duration");
        Assert.Contains(
            inputBatch.Facts,
            static fact => fact.Fact.Key == "good-quantity");
        Assert.Equal(
            Enumerable.Range(1, inputBatch.Facts.Count)
                .Select(static value => new MetricInputPosition((ulong)value)),
            inputBatch.Facts.Select(static fact => fact.Position));

        var aggregationSet = provider.GetRequiredService<
            MetricAggregationProcessingRuntimeSet>();
        Assert.True(await aggregationSet.RunCycleAsync(CancellationToken.None));

        var aggregationStore = provider.GetRequiredService<IMetricAggregationStore>();
        var aggregationRuntime = Assert.Single(aggregationSet.Runtimes);
        var aggregationCheckpoint = await aggregationStore.ReadCheckpointAsync(
            aggregationRuntime.ProcessorId,
            metricStreamId,
            CancellationToken.None);
        Assert.NotNull(aggregationCheckpoint);
        Assert.Equal(inputBatch.ThroughPosition, aggregationCheckpoint.Position);

        foreach (var positioned in inputBatch.Facts)
        {
            var aggregate = await aggregationStore.ReadShiftAggregateAsync(
                aggregationRuntime.ProcessorId,
                new ShiftMetricAggregateKey(
                    machineId,
                    positioned.ShiftOccurrenceId,
                    positioned.Fact.Key),
                CancellationToken.None);
            Assert.NotNull(aggregate);
        }

        var restartedActivity = new ProductionContextProcessingRuntime(
            new ObservationProcessorId("production-context"),
            provider.GetRequiredService<IProductionContextActivityReader>(),
            provider.GetRequiredService<IProductionContextReader>(),
            provider.GetRequiredService<ShiftOccurrenceResolver>(),
            provider.GetRequiredService<PlannedProductionIntervalResolver>(),
            provider.GetRequiredService<IProductionContextProcessingStore>(),
            provider.GetRequiredService<ProductionContextProcessingScope>(),
            batchSize: 100);
        var restartedQuantity = new ProductionQuantityFactProcessingRuntime(
            new ObservationProcessorId("production-quantity"),
            provider.GetRequiredService<IProductionQuantityEvidenceReader>(),
            provider.GetRequiredService<ShiftOccurrenceResolver>(),
            provider.GetRequiredService<IProductionContextProcessingStore>(),
            quantityStreamId,
            batchSize: 100);
        var restartedAggregation = new MetricAggregationProcessingRuntime(
            aggregationRuntime.ProcessorId,
            inputReader,
            aggregationStore,
            metricStreamId,
            batchSize: 100);

        Assert.Equal(
            0,
            await restartedActivity.RunCycleAsync(CancellationToken.None));
        Assert.Equal(
            0,
            await restartedQuantity.RunCycleAsync(CancellationToken.None));
        Assert.Equal(
            0,
            await restartedAggregation.RunCycleAsync(CancellationToken.None));

        var afterRestart = await inputReader.ReadAsync(
            new MetricInputReadRequest(
                metricStreamId,
                afterPosition: null,
                maxCount: 100),
            CancellationToken.None);
        Assert.Equal(inputBatch.Facts, afterRestart.Facts);
    }

    private static async Task SeedActivityAsync(
        InMemoryMachineStateActivityProjectionStore store,
        ObservationStreamId streamId)
    {
        var processorId = new ObservationProcessorId("machine-state-activity");
        var position = new ObservationPosition(1);
        var period = new DurableMachineActivityPeriod(
            processorId,
            position,
            streamId,
            instanceId: 1,
            sequence: 1,
            new MachineActivityPeriod(
                streamId.MachineId,
                MachineState.Running,
                new DateTimeOffset(2026, 8, 27, 10, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 27, 11, 0, 0, TimeSpan.Zero)));
        var projection = new MachineStateActivityProjection(
            processorId,
            streamId,
            position,
            [],
            MachineState.Running,
            activeState: null,
            activeStartedAt: null);

        await store.CommitAsync(
            new MachineStateActivityProjectionCommit(
                expectedProjection: null,
                projection,
                stateChanges: [],
                activityPeriods: [period]),
            CancellationToken.None);
    }

    private static IConfiguration CreateConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Persistence:Provider"] = "InMemory",
                    ["ObservationProcessing:BatchSize"] = "100",
                    ["ObservationProcessing:PollingInterval"] = "00:00:01",
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
                    ["ProductionProcessing:Shift:StartsAtLocal"] = "06:00:00",
                    ["ProductionProcessing:Shift:EndsAtLocal"] = "14:00:00",
                    ["ProductionProcessing:Shift:EffectiveFrom"] = "2026-01-01",
                    ["ProductionProcessing:PlannedProduction:AssignmentId"] =
                        "POT-1",
                    ["ProductionProcessing:PlannedProduction:TimeZoneId"] = "UTC",
                    ["ProductionProcessing:PlannedProduction:StartsAtLocal"] =
                        "06:00:00",
                    ["ProductionProcessing:PlannedProduction:EndsAtLocal"] =
                        "14:00:00",
                    ["ProductionProcessing:PlannedProduction:EffectiveFrom"] =
                        "2026-01-01",
                    ["MetricAggregation:BatchSize"] = "100",
                    ["MetricAggregation:PollingInterval"] = "00:00:01",
                })
            .Build();
}
