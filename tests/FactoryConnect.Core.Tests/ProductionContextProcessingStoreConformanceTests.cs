using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using Xunit;

namespace FactoryConnect.Core.Tests;

public sealed class ProductionContextProcessingStoreConformanceTests
{
    [Fact]
    public async Task StoreRejectsSameDurableIdWithDifferentContentWithoutAdvancingCheckpoint()
    {
        var machineId = new MachineId(new Guid("55555555-5555-5555-5555-555555555555"));
        var streamId = new ObservationStreamId(machineId, "activity");
        var processorId = new ObservationProcessorId("fc025-store");
        var store = new InMemoryProductionContextProcessingStore();
        var firstCheckpoint = new ObservationProcessingCheckpoint(
            processorId,
            streamId,
            new ObservationPosition(1));

        await store.CommitAsync(
            new ProductionContextProcessingCommit
            {
                ExpectedCheckpoint = null,
                NextCheckpoint = firstCheckpoint,
                ContextualizedActivity = [],
                EligibilityIntervals = [],
                MetricFacts = [CreateFact(machineId, 1m)],
            },
            CancellationToken.None);

        var conflicting = new ProductionContextProcessingCommit
        {
            ExpectedCheckpoint = firstCheckpoint,
            NextCheckpoint = new ObservationProcessingCheckpoint(
                processorId,
                streamId,
                new ObservationPosition(2)),
            ContextualizedActivity = [],
            EligibilityIntervals = [],
            MetricFacts = [CreateFact(machineId, 2m)],
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CommitAsync(conflicting, CancellationToken.None));

        var checkpoint = await store.ReadCheckpointAsync(
            processorId,
            streamId,
            CancellationToken.None);
        Assert.NotNull(checkpoint);
        Assert.Equal(new ObservationPosition(1), checkpoint.Position);
        var fact = Assert.Single(store.MetricFacts);
        Assert.Equal(1m, fact.Value);
    }

    private static DurableMetricInputFact CreateFact(MachineId machineId, decimal value) =>
        new()
        {
            Id = new MetricInputFactId("FACT-1"),
            Key = MetricInputFactKeys.RunningDuration,
            Value = value,
            Unit = MetricInputFactUnits.Seconds,
            StartsAtUtc = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero),
            EndsAtUtc = new DateTimeOffset(2026, 8, 26, 8, 0, 1, TimeSpan.Zero),
            CompanyId = new CompanyId("COMP-1"),
            SiteId = new SiteId("SITE-1"),
            ProductionLineId = new ProductionLineId("LINE-1"),
            MachineId = machineId,
            ShiftId = new ShiftId("SHIFT-1"),
            ShiftScheduleAssignmentId = new ShiftScheduleAssignmentId("SHIFT-SCHEDULE-1"),
            IsPlannedProductionTime = true,
            PlannedProductionScheduleAssignmentId = new PlannedProductionScheduleAssignmentId("POT-1"),
        };
}
