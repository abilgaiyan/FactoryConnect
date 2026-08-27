using FactoryConnect.Abstractions;

namespace FactoryConnect.Integration.Tests;

public sealed class MetricAggregationContractTests
{
    private static readonly MachineId MachineOne = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"));

    private static readonly MachineId MachineTwo = new(
        Guid.Parse("22222222-2222-2222-2222-222222222222"));

    [Fact]
    public void MetricInputPositionRejectsZeroAndOrdersByValue()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MetricInputPosition(0));

        var first = new MetricInputPosition(1);
        var second = new MetricInputPosition(2);

        Assert.True(first < second);
        Assert.True(second > first);
    }

    [Fact]
    public void MetricInputStreamIsMachineScoped()
    {
        var first = new MetricInputStreamId(MachineOne, "metrics");
        var replay = new MetricInputStreamId(MachineOne, "metrics");
        var otherMachine = new MetricInputStreamId(
            MachineTwo,
            "metrics");

        Assert.Equal(first, replay);
        Assert.NotEqual(first, otherMachine);
    }

    [Fact]
    public void ShiftOccurrenceIdentityIsDeterministicFromResolvedOwnership()
    {
        var startsAt = new DateTimeOffset(2026, 8, 27, 16, 30, 0, TimeSpan.Zero);
        var endsAt = startsAt.AddHours(8);

        var first = CreateShiftOccurrenceId(startsAt, endsAt);
        var replay = CreateShiftOccurrenceId(startsAt, endsAt);
        var nextOccurrence = CreateShiftOccurrenceId(
            startsAt.AddDays(1),
            endsAt.AddDays(1));

        Assert.Equal(first, replay);
        Assert.NotEqual(first, nextOccurrence);
    }

    [Fact]
    public void ProductionDayIdentityIsSiteAndBusinessDateScoped()
    {
        var siteId = new SiteId("site-1");
        var businessDate = new DateOnly(2026, 8, 27);

        var first = new ProductionDayId(siteId, businessDate);
        var replay = new ProductionDayId(siteId, businessDate);
        var nextDay = new ProductionDayId(siteId, businessDate.AddDays(1));

        Assert.Equal(first, replay);
        Assert.NotEqual(first, nextDay);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ProductionDayId(siteId, default));
    }

    [Fact]
    public void PositionedFactRequiresMatchingMachineAndTemporalOwnership()
    {
        var streamId = new MetricInputStreamId(
            MachineOne,
            "metrics");
        var shiftOccurrenceId = CreateShiftOccurrenceId(
            new DateTimeOffset(2026, 8, 27, 6, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 27, 14, 30, 0, TimeSpan.Zero));
        var productionDayId = new ProductionDayId(
            new SiteId("site-1"),
            new DateOnly(2026, 8, 27));

        var positioned = new PositionedMetricInputFact(
            streamId,
            new MetricInputPosition(1),
            CreateFact(MachineOne),
            shiftOccurrenceId,
            productionDayId);

        Assert.Equal(streamId, positioned.StreamId);
        Assert.Throws<ArgumentException>(() => new PositionedMetricInputFact(
            streamId,
            new MetricInputPosition(2),
            CreateFact(MachineTwo),
            shiftOccurrenceId,
            productionDayId));
    }

    [Fact]
    public void ReadBatchRequiresStrictlyIncreasingStreamPositions()
    {
        var streamId = new MetricInputStreamId(
            MachineOne,
            "metrics");
        var shiftOccurrenceId = CreateShiftOccurrenceId(
            new DateTimeOffset(2026, 8, 27, 6, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 27, 14, 30, 0, TimeSpan.Zero));
        var productionDayId = new ProductionDayId(
            new SiteId("site-1"),
            new DateOnly(2026, 8, 27));
        var fact = CreateFact(MachineOne);

        var first = new PositionedMetricInputFact(
            streamId,
            new MetricInputPosition(2),
            fact,
            shiftOccurrenceId,
            productionDayId);
        var second = new PositionedMetricInputFact(
            streamId,
            new MetricInputPosition(3),
            fact with { Id = new MetricInputFactId("fact-2") },
            shiftOccurrenceId,
            productionDayId);

        var batch = new MetricInputReadBatch(
            streamId,
            new MetricInputPosition(1),
            new MetricInputPosition(3),
            [first, second]);

        Assert.Equal(2, batch.Facts.Count);

        Assert.Throws<ArgumentException>(() => new MetricInputReadBatch(
            streamId,
            new MetricInputPosition(1),
            new MetricInputPosition(3),
            [second, first]));
    }

    [Fact]
    public void EmptyReadWindowCanExplicitlyAdvanceSourceProgress()
    {
        var streamId = new MetricInputStreamId(
            MachineOne,
            "metrics");

        var batch = new MetricInputReadBatch(
            streamId,
            new MetricInputPosition(10),
            new MetricInputPosition(12),
            []);

        Assert.Empty(batch.Facts);
        Assert.Equal(new MetricInputPosition(12), batch.ThroughPosition);
    }

    [Fact]
    public void AggregateKeysDoNotPartitionByUnit()
    {
        var shiftOccurrenceId = CreateShiftOccurrenceId(
            new DateTimeOffset(2026, 8, 27, 6, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 27, 14, 30, 0, TimeSpan.Zero));

        var key = new ShiftMetricAggregateKey(
            MachineOne,
            shiftOccurrenceId,
            "running-duration");
        var value = new MetricAggregateValue(
            60m,
            "seconds",
            1,
            shiftOccurrenceId.StartsAtUtc,
            shiftOccurrenceId.StartsAtUtc.AddMinutes(1));

        Assert.Equal("running-duration", key.MetricInputKey);
        Assert.Equal("seconds", value.Unit);
    }

    private static ShiftOccurrenceId CreateShiftOccurrenceId(
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc) =>
        new(
            new SiteId("site-1"),
            new ShiftScheduleAssignmentId("schedule-1"),
            new ShiftId("shift-a"),
            startsAtUtc,
            endsAtUtc);

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
