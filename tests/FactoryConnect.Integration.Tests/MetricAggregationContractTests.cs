using FactoryConnect.Abstractions;

namespace FactoryConnect.Integration.Tests;

public sealed class MetricAggregationContractTests
{
    [Fact]
    public void Metric_input_position_rejects_zero_and_orders_by_value()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MetricInputPosition(0));

        var first = new MetricInputPosition(1);
        var second = new MetricInputPosition(2);

        Assert.True(first < second);
        Assert.True(second > first);
    }

    [Fact]
    public void Metric_input_stream_is_machine_scoped()
    {
        var machineId = new MachineId("machine-1");

        var first = new MetricInputStreamId(machineId, "metrics");
        var replay = new MetricInputStreamId(machineId, "metrics");
        var otherMachine = new MetricInputStreamId(
            new MachineId("machine-2"),
            "metrics");

        Assert.Equal(first, replay);
        Assert.NotEqual(first, otherMachine);
    }

    [Fact]
    public void Shift_occurrence_identity_is_deterministic_from_resolved_ownership()
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
    public void Production_day_identity_is_site_and_business_date_scoped()
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
    public void Positioned_fact_requires_matching_machine_and_temporal_ownership()
    {
        var streamId = new MetricInputStreamId(
            new MachineId("machine-1"),
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
            CreateFact(new MachineId("machine-1")),
            shiftOccurrenceId,
            productionDayId);

        Assert.Equal(streamId, positioned.StreamId);
        Assert.Throws<ArgumentException>(() => new PositionedMetricInputFact(
            streamId,
            new MetricInputPosition(2),
            CreateFact(new MachineId("machine-2")),
            shiftOccurrenceId,
            productionDayId));
    }

    [Fact]
    public void Read_batch_requires_strictly_increasing_stream_positions()
    {
        var streamId = new MetricInputStreamId(
            new MachineId("machine-1"),
            "metrics");
        var shiftOccurrenceId = CreateShiftOccurrenceId(
            new DateTimeOffset(2026, 8, 27, 6, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 27, 14, 30, 0, TimeSpan.Zero));
        var productionDayId = new ProductionDayId(
            new SiteId("site-1"),
            new DateOnly(2026, 8, 27));
        var fact = CreateFact(new MachineId("machine-1"));

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
    public void Empty_read_window_can_explicitly_advance_source_progress()
    {
        var streamId = new MetricInputStreamId(
            new MachineId("machine-1"),
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
    public void Aggregate_keys_do_not_partition_by_unit()
    {
        var machineId = new MachineId("machine-1");
        var shiftOccurrenceId = CreateShiftOccurrenceId(
            new DateTimeOffset(2026, 8, 27, 6, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 27, 14, 30, 0, TimeSpan.Zero));

        var key = new ShiftMetricAggregateKey(
            machineId,
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
