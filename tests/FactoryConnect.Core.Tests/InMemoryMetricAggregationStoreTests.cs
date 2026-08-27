using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using Xunit;

namespace FactoryConnect.Core.Tests;

public sealed class InMemoryMetricAggregationStoreTests
{
    private static readonly MachineId Machine = new(new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

    [Fact]
    public async Task CommitAtomicallyUpdatesBothProjectionsAndCheckpoint()
    {
        var store = new InMemoryMetricAggregationStore();
        var processor = new MetricAggregationProcessorId("aggregate-m01");
        var stream = MetricInputStreamId.ForMachine(Machine);
        var occurrence = CreateOccurrence();
        var day = CreateProductionDay();
        var first = CreateInput(stream, 1, "FACT-1", 60m, "seconds", occurrence, day, 7);
        var second = CreateInput(stream, 2, "FACT-2", 120m, "seconds", occurrence, day, 8);
        var proposed = new MetricAggregationCheckpoint(processor, stream, new MetricInputPosition(2));

        await store.CommitAsync(
            new MetricAggregationCommit(processor, null, proposed, [first, second]),
            CancellationToken.None);

        var shiftKey = new ShiftMetricAggregateKey(Machine, occurrence, MetricInputFactKeys.RunningDuration);
        var dayKey = new ProductionDayMetricAggregateKey(Machine, day, MetricInputFactKeys.RunningDuration);
        var shift = await store.ReadShiftAggregateAsync(processor, shiftKey, CancellationToken.None);
        var productionDay = await store.ReadProductionDayAggregateAsync(processor, dayKey, CancellationToken.None);
        var checkpoint = await store.ReadCheckpointAsync(processor, stream, CancellationToken.None);

        Assert.NotNull(shift);
        Assert.NotNull(productionDay);
        Assert.Equal(180m, shift.Value);
        Assert.Equal(180m, productionDay.Value);
        Assert.Equal(2, shift.InputCount);
        Assert.Equal(proposed, checkpoint);
    }

    [Fact]
    public async Task IdenticalFactReplayDoesNotInflateTotals()
    {
        var store = new InMemoryMetricAggregationStore();
        var processor = new MetricAggregationProcessorId("aggregate-m01");
        var stream = MetricInputStreamId.ForMachine(Machine);
        var occurrence = CreateOccurrence();
        var day = CreateProductionDay();
        var input = CreateInput(stream, 1, "FACT-1", 60m, "seconds", occurrence, day, 7);
        var firstCheckpoint = new MetricAggregationCheckpoint(processor, stream, new MetricInputPosition(1));

        await store.CommitAsync(
            new MetricAggregationCommit(processor, null, firstCheckpoint, [input]),
            CancellationToken.None);

        var replayCheckpoint = new MetricAggregationCheckpoint(processor, stream, new MetricInputPosition(2));
        await store.CommitAsync(
            new MetricAggregationCommit(processor, firstCheckpoint, replayCheckpoint, [input]),
            CancellationToken.None);

        var key = new ShiftMetricAggregateKey(Machine, occurrence, MetricInputFactKeys.RunningDuration);
        var aggregate = await store.ReadShiftAggregateAsync(processor, key, CancellationToken.None);

        Assert.NotNull(aggregate);
        Assert.Equal(60m, aggregate.Value);
        Assert.Equal(1, aggregate.InputCount);
        Assert.Equal(replayCheckpoint, await store.ReadCheckpointAsync(processor, stream, CancellationToken.None));
    }

    [Fact]
    public async Task ConflictingReplayRejectsEntireCommitWithoutAdvancingCheckpoint()
    {
        var store = new InMemoryMetricAggregationStore();
        var processor = new MetricAggregationProcessorId("aggregate-m01");
        var stream = MetricInputStreamId.ForMachine(Machine);
        var occurrence = CreateOccurrence();
        var day = CreateProductionDay();
        var input = CreateInput(stream, 1, "FACT-1", 60m, "seconds", occurrence, day, 7);
        var firstCheckpoint = new MetricAggregationCheckpoint(processor, stream, new MetricInputPosition(1));
        await store.CommitAsync(
            new MetricAggregationCommit(processor, null, firstCheckpoint, [input]),
            CancellationToken.None);

        var conflicting = CreateInput(stream, 1, "FACT-1", 90m, "seconds", occurrence, day, 7);
        var proposed = new MetricAggregationCheckpoint(processor, stream, new MetricInputPosition(2));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.CommitAsync(
                new MetricAggregationCommit(processor, firstCheckpoint, proposed, [conflicting]),
                CancellationToken.None));

        var key = new ShiftMetricAggregateKey(Machine, occurrence, MetricInputFactKeys.RunningDuration);
        var aggregate = await store.ReadShiftAggregateAsync(processor, key, CancellationToken.None);
        Assert.NotNull(aggregate);
        Assert.Equal(60m, aggregate.Value);
        Assert.Equal(firstCheckpoint, await store.ReadCheckpointAsync(processor, stream, CancellationToken.None));
    }

    [Fact]
    public async Task StaleExpectedCheckpointIsRejectedWithoutMutation()
    {
        var store = new InMemoryMetricAggregationStore();
        var processor = new MetricAggregationProcessorId("aggregate-m01");
        var stream = MetricInputStreamId.ForMachine(Machine);
        var occurrence = CreateOccurrence();
        var day = CreateProductionDay();
        var first = CreateInput(stream, 1, "FACT-1", 60m, "seconds", occurrence, day, 7);
        var checkpoint = new MetricAggregationCheckpoint(processor, stream, new MetricInputPosition(1));
        await store.CommitAsync(
            new MetricAggregationCommit(processor, null, checkpoint, [first]),
            CancellationToken.None);

        var second = CreateInput(stream, 2, "FACT-2", 30m, "seconds", occurrence, day, 8);
        var proposed = new MetricAggregationCheckpoint(processor, stream, new MetricInputPosition(2));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.CommitAsync(
                new MetricAggregationCommit(processor, null, proposed, [second]),
                CancellationToken.None));

        var key = new ShiftMetricAggregateKey(Machine, occurrence, MetricInputFactKeys.RunningDuration);
        var aggregate = await store.ReadShiftAggregateAsync(processor, key, CancellationToken.None);
        Assert.NotNull(aggregate);
        Assert.Equal(60m, aggregate.Value);
        Assert.Equal(checkpoint, await store.ReadCheckpointAsync(processor, stream, CancellationToken.None));
    }

    [Fact]
    public async Task EmptyWindowMayAdvanceCheckpointWithoutCreatingAggregates()
    {
        var store = new InMemoryMetricAggregationStore();
        var processor = new MetricAggregationProcessorId("aggregate-m01");
        var stream = MetricInputStreamId.ForMachine(Machine);
        var checkpoint = new MetricAggregationCheckpoint(processor, stream, new MetricInputPosition(5));

        await store.CommitAsync(
            new MetricAggregationCommit(processor, null, checkpoint, []),
            CancellationToken.None);

        Assert.Equal(checkpoint, await store.ReadCheckpointAsync(processor, stream, CancellationToken.None));
    }

    [Fact]
    public async Task IncompatibleUnitRejectsBothProjectionUpdatesAndCheckpoint()
    {
        var store = new InMemoryMetricAggregationStore();
        var processor = new MetricAggregationProcessorId("aggregate-m01");
        var stream = MetricInputStreamId.ForMachine(Machine);
        var occurrence = CreateOccurrence();
        var day = CreateProductionDay();
        var first = CreateInput(stream, 1, "FACT-1", 60m, "seconds", occurrence, day, 7);
        var firstCheckpoint = new MetricAggregationCheckpoint(processor, stream, new MetricInputPosition(1));
        await store.CommitAsync(
            new MetricAggregationCommit(processor, null, firstCheckpoint, [first]),
            CancellationToken.None);

        var incompatible = CreateInput(stream, 2, "FACT-2", 1m, "minutes", occurrence, day, 8);
        var proposed = new MetricAggregationCheckpoint(processor, stream, new MetricInputPosition(2));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.CommitAsync(
                new MetricAggregationCommit(processor, firstCheckpoint, proposed, [incompatible]),
                CancellationToken.None));

        var shiftKey = new ShiftMetricAggregateKey(Machine, occurrence, MetricInputFactKeys.RunningDuration);
        var dayKey = new ProductionDayMetricAggregateKey(Machine, day, MetricInputFactKeys.RunningDuration);
        var shift = await store.ReadShiftAggregateAsync(processor, shiftKey, CancellationToken.None);
        var productionDay = await store.ReadProductionDayAggregateAsync(processor, dayKey, CancellationToken.None);

        Assert.NotNull(shift);
        Assert.NotNull(productionDay);
        Assert.Equal(60m, shift.Value);
        Assert.Equal(60m, productionDay.Value);
        Assert.Equal(firstCheckpoint, await store.ReadCheckpointAsync(processor, stream, CancellationToken.None));
    }

    [Fact]
    public async Task ProcessorProjectionStateAndProgressRemainIndependent()
    {
        var store = new InMemoryMetricAggregationStore();
        var firstProcessor = new MetricAggregationProcessorId("aggregate-a");
        var secondProcessor = new MetricAggregationProcessorId("aggregate-b");
        var stream = MetricInputStreamId.ForMachine(Machine);
        var occurrence = CreateOccurrence();
        var day = CreateProductionDay();
        var input = CreateInput(stream, 1, "FACT-1", 60m, "seconds", occurrence, day, 7);
        var firstCheckpoint = new MetricAggregationCheckpoint(firstProcessor, stream, new MetricInputPosition(1));
        var secondCheckpoint = new MetricAggregationCheckpoint(secondProcessor, stream, new MetricInputPosition(1));

        await store.CommitAsync(
            new MetricAggregationCommit(firstProcessor, null, firstCheckpoint, [input]),
            CancellationToken.None);
        await store.CommitAsync(
            new MetricAggregationCommit(secondProcessor, null, secondCheckpoint, [input]),
            CancellationToken.None);

        var key = new ShiftMetricAggregateKey(Machine, occurrence, MetricInputFactKeys.RunningDuration);
        var first = await store.ReadShiftAggregateAsync(firstProcessor, key, CancellationToken.None);
        var second = await store.ReadShiftAggregateAsync(secondProcessor, key, CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(60m, first.Value);
        Assert.Equal(60m, second.Value);
        Assert.Equal(firstCheckpoint, await store.ReadCheckpointAsync(firstProcessor, stream, CancellationToken.None));
        Assert.Equal(secondCheckpoint, await store.ReadCheckpointAsync(secondProcessor, stream, CancellationToken.None));
    }

    private static ShiftOccurrenceId CreateOccurrence() =>
        new(
            new SiteId("SITE-1"),
            new ShiftScheduleAssignmentId("SCHEDULE-A"),
            new ShiftId("SHIFT-A"),
            new DateTimeOffset(2026, 8, 27, 6, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 27, 14, 0, 0, TimeSpan.Zero));

    private static ProductionDayId CreateProductionDay() =>
        new(new SiteId("SITE-1"), new DateOnly(2026, 8, 27));

    private static PositionedMetricInputFact CreateInput(
        MetricInputStreamId stream,
        ulong position,
        string factId,
        decimal value,
        string unit,
        ShiftOccurrenceId occurrence,
        ProductionDayId productionDay,
        int hour)
    {
        var startsAt = new DateTimeOffset(2026, 8, 27, hour, 0, 0, TimeSpan.Zero);
        var fact = new DurableMetricInputFact
        {
            Id = new MetricInputFactId(factId),
            Key = MetricInputFactKeys.RunningDuration,
            Value = value,
            Unit = unit,
            StartsAtUtc = startsAt,
            EndsAtUtc = startsAt.AddMinutes(1),
            CompanyId = new CompanyId("COMP-1"),
            SiteId = new SiteId("SITE-1"),
            ProductionLineId = new ProductionLineId("LINE-1"),
            MachineId = Machine,
            ShiftId = occurrence.ShiftId,
            ShiftScheduleAssignmentId = occurrence.ShiftScheduleAssignmentId,
        };

        return new PositionedMetricInputFact(
            stream,
            new MetricInputPosition(position),
            fact,
            occurrence,
            productionDay);
    }
}
