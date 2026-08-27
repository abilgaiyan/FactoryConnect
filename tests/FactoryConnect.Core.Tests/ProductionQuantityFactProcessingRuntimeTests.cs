using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using Xunit;

namespace FactoryConnect.Core.Tests;

public sealed class ProductionQuantityFactProcessingRuntimeTests
{
    [Fact]
    public async Task QuantityRuntimeUsesIndependentCheckpointAndResumesWithoutDuplicates()
    {
        var machineId = new MachineId(new Guid("11111111-1111-1111-1111-111111111111"));
        var streamId = new ObservationStreamId(machineId, "quantity");
        var reader = new InMemoryProductionQuantityEvidenceReader();
        reader.Add(CreateDurableEvidence(machineId, streamId, 1, "Q1"));
        reader.Add(CreateDurableEvidence(machineId, streamId, 2, "Q2"));
        var store = new InMemoryProductionContextProcessingStore();
        var quantityProcessorId = new ObservationProcessorId("quantity-facts");
        var shiftResolver = CreateShiftResolver();

        var first = new ProductionQuantityFactProcessingRuntime(
            quantityProcessorId, reader, shiftResolver, store, streamId, 1);
        Assert.Equal(1, await first.RunCycleAsync());

        var resumed = new ProductionQuantityFactProcessingRuntime(
            quantityProcessorId, reader, shiftResolver, store, streamId, 1);
        Assert.Equal(1, await resumed.RunCycleAsync());
        Assert.Equal(0, await resumed.RunCycleAsync());

        Assert.Equal(6, store.MetricFacts.Count);
        Assert.Equal(6, store.PositionedMetricInputs.Count);
        var checkpoint = await store.ReadCheckpointAsync(
            quantityProcessorId, streamId, CancellationToken.None);
        Assert.NotNull(checkpoint);
        Assert.Equal(new ObservationPosition(2), checkpoint.Position);
    }

    [Fact]
    public async Task ProcessorIdentityIsIndependentForSameStream()
    {
        var machineId = new MachineId(new Guid("11111111-1111-1111-1111-111111111111"));
        var streamId = new ObservationStreamId(machineId, "quantity");
        var reader = new InMemoryProductionQuantityEvidenceReader();
        reader.Add(CreateDurableEvidence(machineId, streamId, 1, "Q1"));
        var store = new InMemoryProductionContextProcessingStore();
        var firstId = new ObservationProcessorId("quantity-a");
        var secondId = new ObservationProcessorId("quantity-b");
        var shiftResolver = CreateShiftResolver();

        await new ProductionQuantityFactProcessingRuntime(
            firstId, reader, shiftResolver, store, streamId, 10).RunCycleAsync();
        await new ProductionQuantityFactProcessingRuntime(
            secondId, reader, shiftResolver, store, streamId, 10).RunCycleAsync();

        var firstCheckpoint = await store.ReadCheckpointAsync(firstId, streamId, CancellationToken.None);
        var secondCheckpoint = await store.ReadCheckpointAsync(secondId, streamId, CancellationToken.None);
        Assert.NotNull(firstCheckpoint);
        Assert.NotNull(secondCheckpoint);
        Assert.Equal(new ObservationPosition(1), firstCheckpoint.Position);
        Assert.Equal(new ObservationPosition(1), secondCheckpoint.Position);
        Assert.Equal(3, store.MetricFacts.Count);
        Assert.Equal(3, store.PositionedMetricInputs.Count);
    }

    private static ShiftOccurrenceResolver CreateShiftResolver()
    {
        var assignment = new ShiftScheduleAssignment
        {
            Id = new ShiftScheduleAssignmentId("SCHEDULE-1"),
            CompanyId = new CompanyId("COMP-1"),
            SiteId = new SiteId("SITE-1"),
            TimeZoneId = new FactoryTimeZoneId("Asia/Kolkata"),
            ShiftId = new ShiftId("SHIFT-1"),
            Name = "SHIFT-1",
            StartsAtLocal = new TimeOnly(6, 0),
            EndsAtLocal = new TimeOnly(15, 0),
            EffectiveFrom = new DateOnly(2026, 8, 26),
        };

        return new ShiftOccurrenceResolver(new InMemoryShiftScheduleReader([assignment]));
    }

    private static DurableProductionQuantityEvidence CreateDurableEvidence(
        MachineId machineId,
        ObservationStreamId streamId,
        ulong position,
        string id) =>
        new(
            new ObservationPosition(position),
            streamId,
            new ProductionQuantityEvidence
            {
                Id = new ProductionQuantityEvidenceId(id),
                CompanyId = new CompanyId("COMP-1"),
                SiteId = new SiteId("SITE-1"),
                ProductionLineId = new ProductionLineId("LINE-1"),
                MachineId = machineId,
                ShiftId = new ShiftId("SHIFT-1"),
                OccurredAtUtc = new DateTimeOffset(2026, 8, 26, 8, (int)position, 0, TimeSpan.Zero),
                PartCountIncrement = 1,
                GoodQuantity = 1,
                RejectedQuantity = 0,
            });
}
