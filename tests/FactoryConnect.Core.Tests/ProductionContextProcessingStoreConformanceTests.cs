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
        var metricStream = MetricInputStreamId.ForMachine(machineId);
        var store = new InMemoryProductionContextProcessingStore();
        var firstFact = CreateFact(machineId, "FACT-1", 1m);
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
                MetricFacts = [firstFact],
                MetricInputs = [CreateAppend(metricStream, firstFact)],
            },
            CancellationToken.None);

        var conflictingFact = CreateFact(machineId, "FACT-1", 2m);
        var conflicting = new ProductionContextProcessingCommit
        {
            ExpectedCheckpoint = firstCheckpoint,
            NextCheckpoint = new ObservationProcessingCheckpoint(
                processorId,
                streamId,
                new ObservationPosition(2)),
            ContextualizedActivity = [],
            EligibilityIntervals = [],
            MetricFacts = [conflictingFact],
            MetricInputs = [CreateAppend(metricStream, conflictingFact)],
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
        var fact = CreateFact(machineId, "FACT-1", 1m);
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

    [Fact]
    public async Task FactWithoutCorrespondingPositionedInputIsRejectedWithoutMutation()
    {
        var fixture = CreateFixture();
        var fact = CreateFact(fixture.MachineId, "FACT-1", 1m);

        await AssertRejectedWithoutMutation(
            fixture,
            new ProductionContextProcessingCommit
            {
                ExpectedCheckpoint = null,
                NextCheckpoint = fixture.NextCheckpoint,
                MetricFacts = [fact],
                MetricInputs = [],
            });
    }

    [Fact]
    public async Task PositionedInputWithoutCorrespondingFactIsRejectedWithoutMutation()
    {
        var fixture = CreateFixture();
        var fact = CreateFact(fixture.MachineId, "FACT-1", 1m);

        await AssertRejectedWithoutMutation(
            fixture,
            new ProductionContextProcessingCommit
            {
                ExpectedCheckpoint = null,
                NextCheckpoint = fixture.NextCheckpoint,
                MetricFacts = [],
                MetricInputs = [CreateAppend(fixture.MetricStream, fact)],
            });
    }

    [Fact]
    public async Task DifferentPayloadForSameIdentityAcrossCollectionsIsRejectedWithoutMutation()
    {
        var fixture = CreateFixture();
        var fact = CreateFact(fixture.MachineId, "FACT-1", 1m);
        var different = fact with { Value = 2m };

        await AssertRejectedWithoutMutation(
            fixture,
            new ProductionContextProcessingCommit
            {
                ExpectedCheckpoint = null,
                NextCheckpoint = fixture.NextCheckpoint,
                MetricFacts = [fact],
                MetricInputs = [CreateAppend(fixture.MetricStream, different)],
            });
    }

    [Fact]
    public async Task EquivalentMetricCollectionsInDifferentOrderingAreAccepted()
    {
        var fixture = CreateFixture();
        var first = CreateFact(fixture.MachineId, "FACT-1", 1m);
        var second = CreateFact(fixture.MachineId, "FACT-2", 2m);

        await fixture.Store.CommitAsync(
            new ProductionContextProcessingCommit
            {
                ExpectedCheckpoint = null,
                NextCheckpoint = fixture.NextCheckpoint,
                MetricFacts = [first, second],
                MetricInputs =
                [
                    CreateAppend(fixture.MetricStream, second),
                    CreateAppend(fixture.MetricStream, first),
                ],
            },
            CancellationToken.None);

        Assert.Equal(2, fixture.Store.MetricFacts.Count);
        Assert.Equal(2, fixture.Store.PositionedMetricInputs.Count);
        var checkpoint = await fixture.Store.ReadCheckpointAsync(
            fixture.ProcessorId,
            fixture.SourceStream,
            CancellationToken.None);
        Assert.Equal(fixture.NextCheckpoint, checkpoint);
    }

    private static async Task AssertRejectedWithoutMutation(
        StoreFixture fixture,
        ProductionContextProcessingCommit commit)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Store.CommitAsync(commit, CancellationToken.None));

        Assert.Empty(fixture.Store.MetricFacts);
        Assert.Empty(fixture.Store.PositionedMetricInputs);
        Assert.Null(await fixture.Store.ReadCheckpointAsync(
            fixture.ProcessorId,
            fixture.SourceStream,
            CancellationToken.None));
    }

    private static StoreFixture CreateFixture()
    {
        var machineId = new MachineId(new Guid("77777777-7777-7777-7777-777777777777"));
        var sourceStream = new ObservationStreamId(machineId, "activity");
        var processorId = new ObservationProcessorId("fc025-equivalence");

        return new StoreFixture(
            machineId,
            sourceStream,
            processorId,
            MetricInputStreamId.ForMachine(machineId),
            new ObservationProcessingCheckpoint(
                processorId,
                sourceStream,
                new ObservationPosition(1)),
            new InMemoryProductionContextProcessingStore());
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

    private static DurableMetricInputFact CreateFact(
        MachineId machineId,
        string factId,
        decimal value) =>
        new()
        {
            Id = new MetricInputFactId(factId),
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

    private sealed record StoreFixture(
        MachineId MachineId,
        ObservationStreamId SourceStream,
        ObservationProcessorId ProcessorId,
        MetricInputStreamId MetricStream,
        ObservationProcessingCheckpoint NextCheckpoint,
        InMemoryProductionContextProcessingStore Store);
}
