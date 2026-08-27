using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Edge;
using FactoryConnect.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class ProductionMetricInputCommitFailureCompositionTests
{
    [Fact]
    public async Task FailedProducerCommitPublishesNoMetricInputAndNoCheckpoint()
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

        await using var provider = services.BuildServiceProvider();
        var evidenceReader = provider.GetRequiredService<
            InMemoryProductionQuantityEvidenceReader>();
        evidenceReader.Add(
            new DurableProductionQuantityEvidence(
                new ObservationPosition(1),
                quantityStream,
                new ProductionQuantityEvidence
                {
                    Id = new ProductionQuantityEvidenceId("Q-FAIL"),
                    CompanyId = new CompanyId("COMP-1"),
                    SiteId = new SiteId("SITE-1"),
                    ProductionLineId = new ProductionLineId("LINE-1"),
                    MachineId = machineId,
                    ShiftId = new ShiftId("SHIFT-1"),
                    OccurredAtUtc = new DateTimeOffset(
                        2026,
                        8,
                        27,
                        10,
                        30,
                        0,
                        TimeSpan.Zero),
                    GoodQuantity = 2,
                }));

        var innerStore = provider.GetRequiredService<
            IProductionContextProcessingStore>();
        var processorId = new ObservationProcessorId("failing-producer");
        var runtime = new ProductionQuantityFactProcessingRuntime(
            processorId,
            provider.GetRequiredService<IProductionQuantityEvidenceReader>(),
            provider.GetRequiredService<ShiftOccurrenceResolver>(),
            new FailingCommitStore(innerStore),
            quantityStream,
            batchSize: 100);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.RunCycleAsync(CancellationToken.None));

        var checkpoint = await innerStore.ReadCheckpointAsync(
            processorId,
            quantityStream,
            CancellationToken.None);
        Assert.Null(checkpoint);

        var inputReader = provider.GetRequiredService<IMetricInputReader>();
        var batch = await inputReader.ReadAsync(
            new MetricInputReadRequest(
                MetricInputStreamId.ForMachine(machineId),
                null,
                100),
            CancellationToken.None);
        Assert.Empty(batch.Facts);
    }

    private sealed class FailingCommitStore(
        IProductionContextProcessingStore inner)
        : IProductionContextProcessingStore
    {
        public Task<ObservationProcessingCheckpoint?> ReadCheckpointAsync(
            ObservationProcessorId processorId,
            ObservationStreamId streamId,
            CancellationToken cancellationToken) =>
            inner.ReadCheckpointAsync(
                processorId,
                streamId,
                cancellationToken);

        public Task CommitAsync(
            ProductionContextProcessingCommit commit,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Injected producer commit failure.");
        }
    }

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
                })
            .Build();
}
