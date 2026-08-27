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

    [Fact]
    public async Task PositionedMetricInputAndCheckpointCommitAtomically()
    {
        var machineId = new MachineId(new Guid("66666666-6666-6666-6666-666666666666"));
        var sourceStream = new ObservationStreamId(machineId, "activity");
        var processorId = new ObservationProcessorId("fc025-atomic-output");
        var metricStream = MetricInputStreamId.ForMachine(machineId);
        var fact = CreateFact(machineId, 1m);
        var append = CreateAppend(metricStream, fact);
        var store = new InMemoryProductionContextProcessingStore();
        var firstCheckpoint = new ObservationProcessingCheckpoint(
            processorId,
            sourceStream,
            new ObservationPosition(1));

        await store.CommitAsync(
            new ProductionContextProcessingCommit
            {
                ExpectedCheckpoint = null,
                NextCheckpoint = firstCheckpoint,
                MetricFacts = [fact],
                MetricInputs = [append],
            },
            CancellationToken.None);

        var positioned = Assert.Single(store.PositionedMetricInputs);
        Assert.Equal(new MetricInputPosition(1), positioned.Position);
        Assert.Equal(fact.Id, positioned.Fact.Id);

        var conflictingFact = fact with { Value = 2m };
        var conflicting = new ProductionContextProcessingCommit
        {
            ExpectedCheckpoint = firstCheckpoint,
            NextCheckpoint = new ObservationProcessingCheckpoint(
                processorId,
                sourceStream,
                new ObservationPosition(2)),
            MetricFacts = [conflictingFact],
            MetricInputs = [CreateAppend(metricStream, conflictingFact)],
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CommitAsync(conflicting, CancellationToken.None));

        var checkpoint = await store.ReadCheckpointAsync(
            processorId,
            sourceStream,
            CancellationToken.None);
        Assert.NotNull(checkpoint);
        Assert.Equal(new ObservationPosition(1), checkpoint.Position);
        Assert.Single(store.PositionedMetricInputs);
        Assert.Equal(1m, Assert.Single(store.MetricFacts).Value);
    }

    private static DurableMetricInputAppend CreateAppend(
        MetricInputStreamId streamId,
        DurableMetricInputFact fact)
    {
        if (fact.ShiftScheduleAssignmentId is not { } scheduleAssignmentId)
        {
            throw new InvalidOperationException("Test fact must include shift schedule assignment identity.");
        }

        var occurrence = new ShiftOccurrenceId(
            fact.SiteId,
            scheduleAssignmentId,
            fact.ShiftId,
            new DateTimeOffset(2026, 8, 26, 7, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 26, 15, 0, 0, TimeSpan.Zero));

        return new DurableMetricInputAppend(
            streamId,
            fact,
            occurrence,
            new ProductionDayId(fact.SiteId, new DateOnly(2026, 8, 26)));
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
