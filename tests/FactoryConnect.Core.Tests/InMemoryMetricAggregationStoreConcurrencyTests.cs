using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using Xunit;

namespace FactoryConnect.Core.Tests;

public sealed class InMemoryMetricAggregationStoreConcurrencyTests
{
    private static readonly MachineId Machine = new(new Guid("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));

    [Fact]
    public async Task NewFactBehindExpectedCheckpointIsRejectedWithoutMutation()
    {
        var store = new InMemoryMetricAggregationStore();
        var processor = new MetricAggregationProcessorId("aggregate-m01");
        var stream = MetricInputStreamId.ForMachine(Machine);
        var occurrence = CreateOccurrence();
        var day = CreateProductionDay();
        var throughFive = new MetricAggregationCheckpoint(processor, stream, new MetricInputPosition(5));

        await store.CommitAsync(
            new MetricAggregationCommit(processor, null, throughFive, []),
            CancellationToken.None);

        var late = CreateInput(stream, 4, "FACT-LATE", 60m, occurrence, day, 7);
        var proposed = new MetricAggregationCheckpoint(processor, stream, new MetricInputPosition(6));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.CommitAsync(
                new MetricAggregationCommit(processor, throughFive, proposed, [late]),
                CancellationToken.None));

        var shiftKey = new ShiftMetricAggregateKey(Machine, occurrence, MetricInputFactKeys.RunningDuration);
        var dayKey = new ProductionDayMetricAggregateKey(Machine, day, MetricInputFactKeys.RunningDuration);
        Assert.Null(await store.ReadShiftAggregateAsync(processor, shiftKey, CancellationToken.None));
        Assert.Null(await store.ReadProductionDayAggregateAsync(processor, dayKey, CancellationToken.None));
        Assert.Equal(throughFive, await store.ReadCheckpointAsync(processor, stream, CancellationToken.None));
    }

    [Fact]
    public async Task NewFactsStrictlyAfterExpectedAndThroughProposedAreApplied()
    {
        var store = new InMemoryMetricAggregationStore();
        var processor = new MetricAggregationProcessorId("aggregate-m01");
        var stream = MetricInputStreamId.ForMachine(Machine);
        var occurrence = CreateOccurrence();
        var day = CreateProductionDay();
        var first = CreateInput(stream, 1, "FACT-1", 10m, occurrence, day, 7);
        var firstCheckpoint = new MetricAggregationCheckpoint(processor, stream, new MetricInputPosition(1));
        await store.CommitAsync(
            new MetricAggregationCommit(processor, null, firstCheckpoint, [first]),
            CancellationToken.None);

        var second = CreateInput(stream, 2, "FACT-2", 20m, occurrence, day, 8);
        var third = CreateInput(stream, 3, "FACT-3", 30m, occurrence, day, 9);
        var throughThree = new MetricAggregationCheckpoint(processor, stream, new MetricInputPosition(3));
        await store.CommitAsync(
            new MetricAggregationCommit(processor, firstCheckpoint, throughThree, [second, third]),
            CancellationToken.None);

        var key = new ShiftMetricAggregateKey(Machine, occurrence, MetricInputFactKeys.RunningDuration);
        var aggregate = await store.ReadShiftAggregateAsync(processor, key, CancellationToken.None);
        Assert.NotNull(aggregate);
        Assert.Equal(60m, aggregate.Value);
        Assert.Equal(3, aggregate.InputCount);
        Assert.Equal(throughThree, await store.ReadCheckpointAsync(processor, stream, CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentCommitsWithSameExpectedCheckpointAllowExactlyOneWinner()
    {
        var store = new InMemoryMetricAggregationStore();
        var processor = new MetricAggregationProcessorId("aggregate-m01");
        var stream = MetricInputStreamId.ForMachine(Machine);
        var occurrence = CreateOccurrence();
        var day = CreateProductionDay();
        var first = CreateInput(stream, 1, "FACT-1", 60m, occurrence, day, 7);
        var firstCheckpoint = new MetricAggregationCheckpoint(processor, stream, new MetricInputPosition(1));
        await store.CommitAsync(
            new MetricAggregationCommit(processor, null, firstCheckpoint, [first]),
            CancellationToken.None);

        var proposed = new MetricAggregationCheckpoint(processor, stream, new MetricInputPosition(2));
        var second = new MetricAggregationCommit(
            processor,
            firstCheckpoint,
            proposed,
            [CreateInput(stream, 2, "FACT-2", 30m, occurrence, day, 8)]);
        var competing = new MetricAggregationCommit(
            processor,
            firstCheckpoint,
            proposed,
            [CreateInput(stream, 2, "FACT-3", 40m, occurrence, day, 9)]);

        var outcomes = await Task.WhenAll(
            Task.Run(() => TryCommitAsync(store, second)),
            Task.Run(() => TryCommitAsync(store, competing)));

        Assert.Equal(1, outcomes.Count(static succeeded => succeeded));

        var key = new ShiftMetricAggregateKey(Machine, occurrence, MetricInputFactKeys.RunningDuration);
        var aggregate = await store.ReadShiftAggregateAsync(processor, key, CancellationToken.None);
        Assert.NotNull(aggregate);
        Assert.Contains(aggregate.Value, new[] { 90m, 100m });
        Assert.Equal(2, aggregate.InputCount);
        Assert.Equal(proposed, await store.ReadCheckpointAsync(processor, stream, CancellationToken.None));
    }

    [Fact]
    public async Task PersistedAggregateOverflowRejectsEntireCommit()
    {
        var store = new InMemoryMetricAggregationStore();
        var processor = new MetricAggregationProcessorId("aggregate-m01");
        var stream = MetricInputStreamId.ForMachine(Machine);
        var occurrence = CreateOccurrence();
        var day = CreateProductionDay();
        var first = CreateInput(stream, 1, "FACT-1", decimal.MaxValue, occurrence, day, 7);
        var firstCheckpoint = new MetricAggregationCheckpoint(processor, stream, new MetricInputPosition(1));
        await store.CommitAsync(
            new MetricAggregationCommit(processor, null, firstCheckpoint, [first]),
            CancellationToken.None);

        var overflow = CreateInput(stream, 2, "FACT-2", 1m, occurrence, day, 8);
        var proposed = new MetricAggregationCheckpoint(processor, stream, new MetricInputPosition(2));

        await Assert.ThrowsAsync<OverflowException>(async () =>
            await store.CommitAsync(
                new MetricAggregationCommit(processor, firstCheckpoint, proposed, [overflow]),
                CancellationToken.None));

        var shiftKey = new ShiftMetricAggregateKey(Machine, occurrence, MetricInputFactKeys.RunningDuration);
        var dayKey = new ProductionDayMetricAggregateKey(Machine, day, MetricInputFactKeys.RunningDuration);
        var shift = await store.ReadShiftAggregateAsync(processor, shiftKey, CancellationToken.None);
        var productionDay = await store.ReadProductionDayAggregateAsync(processor, dayKey, CancellationToken.None);
        Assert.NotNull(shift);
        Assert.NotNull(productionDay);
        Assert.Equal(decimal.MaxValue, shift.Value);
        Assert.Equal(decimal.MaxValue, productionDay.Value);
        Assert.Equal(firstCheckpoint, await store.ReadCheckpointAsync(processor, stream, CancellationToken.None));
    }

    [Fact]
    public async Task ReusedPositionForDifferentFactIsRejectedWithoutMutation()
    {
        var store = new InMemoryMetricAggregationStore();
        var processor = new MetricAggregationProcessorId("aggregate-m01");
        var stream = MetricInputStreamId.ForMachine(Machine);
        var occurrence = CreateOccurrence();
        var day = CreateProductionDay();
        var first = CreateInput(stream, 1, "FACT-1", 60m, occurrence, day, 7);
        var firstCheckpoint = new MetricAggregationCheckpoint(processor, stream, new MetricInputPosition(1));
        await store.CommitAsync(
            new MetricAggregationCommit(processor, null, firstCheckpoint, [first]),
            CancellationToken.None);

        var conflictingPosition = CreateInput(stream, 1, "FACT-2", 30m, occurrence, day, 8);
        var proposed = new MetricAggregationCheckpoint(processor, stream, new MetricInputPosition(2));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.CommitAsync(
                new MetricAggregationCommit(processor, firstCheckpoint, proposed, [conflictingPosition]),
                CancellationToken.None));

        var key = new ShiftMetricAggregateKey(Machine, occurrence, MetricInputFactKeys.RunningDuration);
        var aggregate = await store.ReadShiftAggregateAsync(processor, key, CancellationToken.None);
        Assert.NotNull(aggregate);
        Assert.Equal(60m, aggregate.Value);
        Assert.Equal(1, aggregate.InputCount);
        Assert.Equal(firstCheckpoint, await store.ReadCheckpointAsync(processor, stream, CancellationToken.None));
    }

    private static async Task<bool> TryCommitAsync(
        InMemoryMetricAggregationStore store,
        MetricAggregationCommit commit)
    {
        try
        {
            await store.CommitAsync(commit, CancellationToken.None);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
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
            Unit = MetricInputFactUnits.Seconds,
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
