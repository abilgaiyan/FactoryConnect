using FactoryConnect.Abstractions;
using FactoryConnect.Infrastructure;

namespace FactoryConnect.Integration.Tests;

public sealed class MetricInputDurableHandoffTests
{
    private static readonly MachineId MachineOne = new(new Guid("11111111-1111-1111-1111-111111111111"));
    private static readonly MachineId MachineTwo = new(new Guid("22222222-2222-2222-2222-222222222222"));

    [Fact]
    public async Task AppendAssignsPositionWithoutResolvingTemporalOwnership()
    {
        var store = new InMemoryMetricInputStore();
        var append = CreateAppend();

        var positioned = await store.AppendAsync(append, CancellationToken.None);

        Assert.Equal(new MetricInputPosition(1), positioned.Position);
        Assert.Equal(append.ShiftOccurrenceId, positioned.ShiftOccurrenceId);
        Assert.Equal(append.ProductionDayId, positioned.ProductionDayId);
        Assert.Equal(append.Fact, positioned.Fact);
    }

    [Fact]
    public async Task IdenticalReplayPreservesOriginalPosition()
    {
        var store = new InMemoryMetricInputStore();
        var append = CreateAppend();

        var first = await store.AppendAsync(append, CancellationToken.None);
        var replay = await store.AppendAsync(append, CancellationToken.None);

        Assert.Equal(first, replay);
        Assert.Equal(new MetricInputPosition(1), replay.Position);

        var batch = await store.ReadAsync(
            new MetricInputReadRequest(append.StreamId, null, 10),
            CancellationToken.None);

        Assert.Single(batch.Facts);
    }

    [Fact]
    public async Task ConflictingIdentityReuseIsRejected()
    {
        var store = new InMemoryMetricInputStore();
        var append = CreateAppend();
        await store.AppendAsync(append, CancellationToken.None);

        var conflicting = new DurableMetricInputAppend(
            append.StreamId,
            append.Fact with { Value = append.Fact.Value + 1m },
            append.ShiftOccurrenceId,
            append.ProductionDayId);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.AppendAsync(conflicting, CancellationToken.None));
    }

    [Fact]
    public void MissingScheduleLineageIsRejected()
    {
        var append = CreateAppend();

        Assert.Throws<ArgumentException>(() => new DurableMetricInputAppend(
            append.StreamId,
            append.Fact with { ShiftScheduleAssignmentId = null },
            append.ShiftOccurrenceId,
            append.ProductionDayId));
    }

    [Fact]
    public void MismatchedSiteShiftAndScheduleAreRejected()
    {
        var append = CreateAppend();

        Assert.Throws<ArgumentException>(() => new DurableMetricInputAppend(
            append.StreamId,
            append.Fact with { SiteId = new SiteId("site-2") },
            append.ShiftOccurrenceId,
            append.ProductionDayId));

        Assert.Throws<ArgumentException>(() => new DurableMetricInputAppend(
            append.StreamId,
            append.Fact with { ShiftId = new ShiftId("shift-b") },
            append.ShiftOccurrenceId,
            append.ProductionDayId));

        Assert.Throws<ArgumentException>(() => new DurableMetricInputAppend(
            append.StreamId,
            append.Fact with
            {
                ShiftScheduleAssignmentId = new ShiftScheduleAssignmentId("schedule-2")
            },
            append.ShiftOccurrenceId,
            append.ProductionDayId));
    }

    [Fact]
    public void FactOutsideShiftOccurrenceIsRejected()
    {
        var append = CreateAppend();

        Assert.Throws<ArgumentException>(() => new DurableMetricInputAppend(
            append.StreamId,
            append.Fact with
            {
                StartsAtUtc = append.ShiftOccurrenceId.EndsAtUtc,
                EndsAtUtc = append.ShiftOccurrenceId.EndsAtUtc.AddMinutes(1)
            },
            append.ShiftOccurrenceId,
            append.ProductionDayId));
    }

    [Fact]
    public void OvernightOccurrenceAcceptsContainedFactAfterMidnight()
    {
        var streamId = new MetricInputStreamId(MachineOne, "metrics");
        var occurrence = new ShiftOccurrenceId(
            new SiteId("site-1"),
            new ShiftScheduleAssignmentId("schedule-night"),
            new ShiftId("shift-night"),
            new DateTimeOffset(2026, 8, 27, 18, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 28, 2, 30, 0, TimeSpan.Zero));
        var fact = CreateFact(MachineOne) with
        {
            ShiftId = new ShiftId("shift-night"),
            ShiftScheduleAssignmentId = new ShiftScheduleAssignmentId("schedule-night"),
            StartsAtUtc = new DateTimeOffset(2026, 8, 28, 1, 0, 0, TimeSpan.Zero),
            EndsAtUtc = new DateTimeOffset(2026, 8, 28, 1, 1, 0, TimeSpan.Zero)
        };

        var append = new DurableMetricInputAppend(
            streamId,
            fact,
            occurrence,
            new ProductionDayId(new SiteId("site-1"), new DateOnly(2026, 8, 27)));

        Assert.Equal(new DateOnly(2026, 8, 27), append.ProductionDayId.BusinessDate);
    }

    [Fact]
    public void DuplicateFactIdentityAtDifferentPositionsIsRejected()
    {
        var append = CreateAppend();
        var first = new PositionedMetricInputFact(
            append.StreamId,
            new MetricInputPosition(1),
            append.Fact,
            append.ShiftOccurrenceId,
            append.ProductionDayId);
        var duplicate = new PositionedMetricInputFact(
            append.StreamId,
            new MetricInputPosition(2),
            append.Fact,
            append.ShiftOccurrenceId,
            append.ProductionDayId);

        Assert.Throws<ArgumentException>(() => new MetricInputReadBatch(
            append.StreamId,
            null,
            new MetricInputPosition(2),
            [first, duplicate]));
    }

    [Fact]
    public void NonZeroOffsetShiftOccurrenceIdentityIsRejected()
    {
        var offset = TimeSpan.FromHours(5.5);

        Assert.Throws<ArgumentException>(() => new ShiftOccurrenceId(
            new SiteId("site-1"),
            new ShiftScheduleAssignmentId("schedule-1"),
            new ShiftId("shift-a"),
            new DateTimeOffset(2026, 8, 27, 12, 0, 0, offset),
            new DateTimeOffset(2026, 8, 27, 20, 0, 0, offset)));
    }

    [Fact]
    public void ReadRequestRejectsCheckpointFromAnotherStream()
    {
        var firstStream = new MetricInputStreamId(MachineOne, "metrics");
        var secondStream = new MetricInputStreamId(MachineTwo, "metrics");
        var checkpoint = new MetricAggregationCheckpoint(
            new MetricAggregationProcessorId("aggregate-machine-2"),
            secondStream,
            new MetricInputPosition(5));

        Assert.Throws<ArgumentException>(() =>
            MetricInputReadRequest.FromCheckpoint(firstStream, checkpoint, 10));
    }

    [Fact]
    public void ReadBatchRejectsFactsFromAnotherStream()
    {
        var append = CreateAppend();
        var otherStream = new MetricInputStreamId(MachineTwo, "metrics");
        var otherFact = CreateFact(MachineTwo);
        var positioned = new PositionedMetricInputFact(
            otherStream,
            new MetricInputPosition(1),
            otherFact,
            append.ShiftOccurrenceId,
            append.ProductionDayId);

        Assert.Throws<ArgumentException>(() => new MetricInputReadBatch(
            append.StreamId,
            null,
            new MetricInputPosition(1),
            [positioned]));
    }

    [Fact]
    public void AggregateValueRejectsInvalidUnitCountAndTimestampRange()
    {
        var first = new DateTimeOffset(2026, 8, 27, 6, 30, 0, TimeSpan.Zero);
        var last = first.AddMinutes(1);

        Assert.Throws<ArgumentException>(() =>
            new MetricAggregateValue(1m, " ", 1, first, last));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MetricAggregateValue(1m, "seconds", 0, first, last));
        Assert.Throws<ArgumentException>(() =>
            new MetricAggregateValue(1m, "seconds", 1, last, first));
    }

    [Fact]
    public void MetricInputStreamRejectsFactFromAnotherMachine()
    {
        var append = CreateAppend();

        Assert.Throws<ArgumentException>(() => new DurableMetricInputAppend(
            append.StreamId,
            CreateFact(MachineTwo),
            append.ShiftOccurrenceId,
            append.ProductionDayId));
    }

    private static DurableMetricInputAppend CreateAppend()
    {
        var streamId = new MetricInputStreamId(MachineOne, "metrics");
        var occurrence = new ShiftOccurrenceId(
            new SiteId("site-1"),
            new ShiftScheduleAssignmentId("schedule-1"),
            new ShiftId("shift-a"),
            new DateTimeOffset(2026, 8, 27, 6, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 27, 14, 30, 0, TimeSpan.Zero));

        return new DurableMetricInputAppend(
            streamId,
            CreateFact(MachineOne),
            occurrence,
            new ProductionDayId(new SiteId("site-1"), new DateOnly(2026, 8, 27)));
    }

    private static DurableMetricInputFact CreateFact(MachineId machineId) =>
        new()
        {
            Id = new MetricInputFactId("fact-1"),
            Key = "running-duration",
            Value = 60m,
            Unit = "seconds",
            StartsAtUtc = new DateTimeOffset(2026, 8, 27, 6, 30, 0, TimeSpan.Zero),
            EndsAtUtc = new DateTimeOffset(2026, 8, 27, 6, 31, 0, TimeSpan.Zero),
            CompanyId = new CompanyId("company-1"),
            SiteId = new SiteId("site-1"),
            MachineId = machineId,
            ShiftId = new ShiftId("shift-a"),
            ShiftScheduleAssignmentId = new ShiftScheduleAssignmentId("schedule-1")
        };
}
