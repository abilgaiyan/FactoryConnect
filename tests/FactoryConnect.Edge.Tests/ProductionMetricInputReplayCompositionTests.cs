using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Edge;
using FactoryConnect.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class ProductionMetricInputReplayCompositionTests
{
    [Fact]
    public async Task IdenticalProducerReplayPreservesPositionAndConflictDoesNotAdvance()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var activityStream = new ObservationStreamId(machineId, "activity");
        var quantityStream = new ObservationStreamId(
            machineId,
            "production-quantity");
        var configuration = CreateConfiguration();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFactoryConnectEdgePersistence(configuration);
        services.AddSingleton<InMemoryMachineStateActivityProjectionStore>();
        services.AddFactoryConnectProductionMetricInputs(
            configuration,
            activityStream);
        services.AddFactoryConnectMetricAggregation(
            configuration,
            [machineId]);

        await using var provider = services.BuildServiceProvider();
        var evidenceReader = provider.GetRequiredService<
            InMemoryProductionQuantityEvidenceReader>();
        evidenceReader.Add(CreateEvidence(machineId, quantityStream, 1, 3));

        var producers = provider.GetRequiredService<ProductionMetricInputRuntimeSet>();
        var quantityRuntime = Assert.Single(producers.QuantityRuntimes);
        Assert.Equal(
            1,
            await quantityRuntime.RunCycleAsync(CancellationToken.None));

        var inputReader = provider.GetRequiredService<IMetricInputReader>();
        var metricStream = MetricInputStreamId.ForMachine(machineId);
        var firstBatch = await ReadAllAsync(inputReader, metricStream);
        var firstFact = Assert.Single(firstBatch.Facts);
        Assert.Equal(new MetricInputPosition(1), firstFact.Position);

        var aggregationSet = provider.GetRequiredService<
            MetricAggregationProcessingRuntimeSet>();
        var aggregationRuntime = Assert.Single(aggregationSet.Runtimes);
        Assert.True(await aggregationSet.RunCycleAsync(CancellationToken.None));

        evidenceReader.Add(CreateEvidence(machineId, quantityStream, 2, 3));
        Assert.Equal(
            1,
            await quantityRuntime.RunCycleAsync(CancellationToken.None));

        var afterIdenticalReplay = await ReadAllAsync(inputReader, metricStream);
        Assert.Single(afterIdenticalReplay.Facts);
        Assert.Equal(firstFact, afterIdenticalReplay.Facts[0]);
        Assert.Equal(
            0,
            await aggregationRuntime.RunCycleAsync(CancellationToken.None));

        var productionStore = provider.GetRequiredService<
            IProductionContextProcessingStore>();
        var checkpointAfterReplay = await productionStore.ReadCheckpointAsync(
            quantityRuntime.ProcessorId,
            quantityStream,
            CancellationToken.None);
        Assert.NotNull(checkpointAfterReplay);
        Assert.Equal(new ObservationPosition(2), checkpointAfterReplay.Position);

        evidenceReader.Add(CreateEvidence(machineId, quantityStream, 3, 4));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => quantityRuntime.RunCycleAsync(CancellationToken.None));

        var checkpointAfterConflict = await productionStore.ReadCheckpointAsync(
            quantityRuntime.ProcessorId,
            quantityStream,
            CancellationToken.None);
        Assert.Equal(checkpointAfterReplay, checkpointAfterConflict);

        var afterConflict = await ReadAllAsync(inputReader, metricStream);
        Assert.Equal(afterIdenticalReplay.Facts, afterConflict.Facts);
        Assert.Equal(
            0,
            await aggregationRuntime.RunCycleAsync(CancellationToken.None));
    }

    private static DurableProductionQuantityEvidence CreateEvidence(
        MachineId machineId,
        ObservationStreamId streamId,
        ulong sourcePosition,
        int goodQuantity) =>
        new(
            new ObservationPosition(sourcePosition),
            streamId,
            new ProductionQuantityEvidence
            {
                Id = new ProductionQuantityEvidenceId("Q-REPLAY"),
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
                GoodQuantity = goodQuantity,
            });

    private static Task<MetricInputReadBatch> ReadAllAsync(
        IMetricInputReader reader,
        MetricInputStreamId streamId) =>
        reader.ReadAsync(
            new MetricInputReadRequest(streamId, null, 100),
            CancellationToken.None).AsTask();

    private static IConfiguration CreateConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Persistence:Provider"] = "InMemory",
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
