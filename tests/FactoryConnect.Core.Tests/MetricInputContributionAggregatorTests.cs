using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using Xunit;

namespace FactoryConnect.Core.Tests;

public sealed class MetricInputContributionAggregatorTests
{
    private static readonly MachineId MachineOne = new(new Guid("11111111-1111-1111-1111-111111111111"));
    private static readonly MachineId MachineTwo = new(new Guid("22222222-2222-2222-2222-222222222222"));

    [Fact]
    public void SeveralInputsAggregateIntoShiftAndProductionDayTotals()
    {
        var stream = MetricInputStreamId.ForMachine(MachineOne);
        var occurrence = CreateOccurrence("SHIFT-A", "SCHEDULE-A", 6, 14);
        var day = new ProductionDayId(new SiteId("SITE-1"), new DateOnly(2026, 8, 27));
        var inputs = new[]
        {
            CreateInput(stream, 1, "FACT-1", MetricInputFactKeys.RunningDuration, 60m, "seconds", occurrence, day, 7, 0),
            CreateInput(stream, 2, "FACT-2", MetricInputFactKeys.RunningDuration, 120m, "seconds", occurrence, day, 8, 0),
        };

        var result = MetricInputContributionAggregator.Aggregate(stream, inputs);

        var shift = Assert.Single(result.ShiftContributions);
        var productionDay = Assert.Single(result.ProductionDayContributions);
        Assert.Equal(180m, shift.Value.Value);
        Assert.Equal(180m, productionDay.Value.Value);
        Assert.Equal(2, shift.Value.InputCount);
        Assert.Equal("seconds", shift.Value.Unit);
    }

    [Fact]
    public void MetricKeysShiftOccurrencesAndProductionDaysRemainIsolated()
    {
        var stream = MetricInputStreamId.ForMachine(MachineOne);
        var shiftA = CreateOccurrence("SHIFT-A", "SCHEDULE-A", 6, 14);
        var shiftB = CreateOccurrence("SHIFT-B", "SCHEDULE-B", 14, 22);
        var dayOne = new ProductionDayId(new SiteId("SITE-1"), new DateOnly(2026, 8, 27));
        var dayTwo = new ProductionDayId(new SiteId("SITE-1"), new DateOnly(2026, 8, 28));
        var inputs = new[]
        {
            CreateInput(stream, 1, "FACT-1", "running-duration", 10m, "seconds", shiftA, dayOne, 7, 0),
            CreateInput(stream, 2, "FACT-2", "idle-duration", 20m, "seconds", shiftA, dayOne, 8, 0),
            CreateInput(stream, 3, "FACT-3", "running-duration", 30m, "seconds", shiftB, dayOne, 15, 0),
            CreateInput(stream, 4, "FACT-4", "running-duration", 40m, "seconds", shiftB, dayTwo, 16, 0),
        };

        var result = MetricInputContributionAggregator.Aggregate(stream, inputs);

        Assert.Equal(3, result.ShiftContributions.Count);
        Assert.Equal(3, result.ProductionDayContributions.Count);
    }

    [Fact]
    public void EquivalentInputSetsProduceIdenticalOrderedResultsRegardlessOfInputOrder()
    {
        var stream = MetricInputStreamId.ForMachine(MachineOne);
        var occurrence = CreateOccurrence("SHIFT-A", "SCHEDULE-A", 6, 14);
        var day = new ProductionDayId(new SiteId("SITE-1"), new DateOnly(2026, 8, 27));
        var first = CreateInput(stream, 1, "FACT-A", "running-duration", 10m, "seconds", occurrence, day, 9, 0);
        var second = CreateInput(stream, 2, "FACT-B", "running-duration", 20m, "seconds", occurrence, day, 7, 0);

        var ordered = MetricInputContributionAggregator.Aggregate(stream, [first, second]);
        var reversed = MetricInputContributionAggregator.Aggregate(stream, [second, first]);

        Assert.Equal(ordered.ShiftContributions, reversed.ShiftContributions);
        Assert.Equal(ordered.ProductionDayContributions, reversed.ProductionDayContributions);
        var aggregate = Assert.Single(ordered.ShiftContributions).Value;
        Assert.Equal(second.Fact.StartsAtUtc, aggregate.FirstInputTimestamp);
        Assert.Equal(first.Fact.EndsAtUtc, aggregate.LastInputTimestamp);
    }

    [Fact]
    public void ContributionContractsRejectNullStateAndSnapshotCollections()
    {
        var occurrence = CreateOccurrence("SHIFT-A", "SCHEDULE-A", 6, 14);
        var day = new ProductionDayId(new SiteId("SITE-1"), new DateOnly(2026, 8, 27));
        var shiftKey = new ShiftMetricAggregateKey(MachineOne, occurrence, "running-duration");
        var dayKey = new ProductionDayMetricAggregateKey(MachineOne, day, "running-duration");
        var timestamp = new DateTimeOffset(2026, 8, 27, 7, 0, 0, TimeSpan.Zero);
        var value = new MetricAggregateValue(60m, "seconds", 1, timestamp, timestamp.AddMinutes(1));

        Assert.Throws<ArgumentNullException>(() => new ShiftMetricAggregateContribution(null!, value));
        Assert.Throws<ArgumentNullException>(() => new ShiftMetricAggregateContribution(shiftKey, null!));
        Assert.Throws<ArgumentNullException>(() => new ProductionDayMetricAggregateContribution(null!, value));
        Assert.Throws<ArgumentNullException>(() => new ProductionDayMetricAggregateContribution(dayKey, null!));
        Assert.Throws<ArgumentNullException>(() => new MetricAggregateContributionSet(null!, []));
        Assert.Throws<ArgumentNullException>(() => new MetricAggregateContributionSet([], null!));
        Assert.Throws<ArgumentException>(() => new MetricAggregateContributionSet(
            new List<ShiftMetricAggregateContribution> { null! },
            []));
        Assert.Throws<ArgumentException>(() => new MetricAggregateContributionSet(
            [],
            new List<ProductionDayMetricAggregateContribution> { null! }));

        var shiftContribution = new ShiftMetricAggregateContribution(shiftKey, value);
        var dayContribution = new ProductionDayMetricAggregateContribution(dayKey, value);
        var shifts = new List<ShiftMetricAggregateContribution> { shiftContribution };
        var days = new List<ProductionDayMetricAggregateContribution> { dayContribution };
        var result = new MetricAggregateContributionSet(shifts, days);

        shifts.Clear();
        days.Clear();

        Assert.Single(result.ShiftContributions);
        Assert.Single(result.ProductionDayContributions);
    }

    [Fact]
    public void AggregateIdentityProvidesTotalCanonicalOrderingIndependentOfPositions()
    {
        var stream = MetricInputStreamId.ForMachine(MachineOne);
        var siteA = new SiteId("SITE-A");
        var siteB = new SiteId("SITE-B");
        var occurrenceA = CreateOccurrence("SHIFT-A", "SCHEDULE-A", 6, 14, siteA);
        var occurrenceB = CreateOccurrence("SHIFT-B", "SCHEDULE-B", 6, 14, siteB);
        var dayA = new ProductionDayId(siteA, new DateOnly(2026, 8, 27));
        var dayB = new ProductionDayId(siteB, new DateOnly(2026, 8, 27));

        var firstSet = new[]
        {
            CreateInput(stream, 1, "FACT-B1", "running-duration", 10m, "seconds", occurrenceB, dayB, 7, 0),
            CreateInput(stream, 2, "FACT-A1", "running-duration", 10m, "seconds", occurrenceA, dayA, 7, 0),
        };
        var secondSet = new[]
        {
            CreateInput(stream, 1, "FACT-A2", "running-duration", 10m, "seconds", occurrenceA, dayA, 7, 0),
            CreateInput(stream, 2, "FACT-B2", "running-duration", 10m, "seconds", occurrenceB, dayB, 7, 0),
        };

        var first = MetricInputContributionAggregator.Aggregate(stream, firstSet);
        var second = MetricInputContributionAggregator.Aggregate(stream, secondSet);

        Assert.Equal(first.ShiftContributions, second.ShiftContributions);
        Assert.Equal(first.ProductionDayContributions, second.ProductionDayContributions);
        Assert.Equal("SITE-A", first.ShiftContributions[0].Key.ShiftOccurrenceId.SiteId.Value);
        Assert.Equal("SITE-B", first.ShiftContributions[1].Key.ShiftOccurrenceId.SiteId.Value);
        Assert.Equal("SITE-A", first.ProductionDayContributions[0].Key.ProductionDayId.SiteId.Value);
        Assert.Equal("SITE-B", first.ProductionDayContributions[1].Key.ProductionDayId.SiteId.Value);
    }

    [Fact]
    public void SameAggregateKeyWithDifferentUnitsIsRejected()
    {
        var stream = MetricInputStreamId.ForMachine(MachineOne);
        var occurrence = CreateOccurrence("SHIFT-A", "SCHEDULE-A", 6, 14);
        var day = new ProductionDayId(new SiteId("SITE-1"), new DateOnly(2026, 8, 27));
        var seconds = CreateInput(stream, 1, "FACT-1", "running-duration", 60m, "seconds", occurrence, day, 7, 0);
        var minutes = CreateInput(stream, 2, "FACT-2", "running-duration", 1m, "minutes", occurrence, day, 8, 0);

        Assert.Throws<InvalidOperationException>(() =>
            MetricInputContributionAggregator.Aggregate(stream, [seconds, minutes]));
    }

    [Fact]
    public void DecimalOverflowIsChecked()
    {
        var stream = MetricInputStreamId.ForMachine(MachineOne);
        var occurrence = CreateOccurrence("SHIFT-A", "SCHEDULE-A", 6, 14);
        var day = new ProductionDayId(new SiteId("SITE-1"), new DateOnly(2026, 8, 27));
        var first = CreateInput(stream, 1, "FACT-1", "quantity", decimal.MaxValue, "pieces", occurrence, day, 7, 0);
        var second = CreateInput(stream, 2, "FACT-2", "quantity", 1m, "pieces", occurrence, day, 8, 0);

        Assert.Throws<OverflowException>(() =>
            MetricInputContributionAggregator.Aggregate(stream, [first, second]));
    }

    [Fact]
    public void InputOutsideConfiguredMachineStreamIsRejected()
    {
        var stream = MetricInputStreamId.ForMachine(MachineOne);
        var otherStream = MetricInputStreamId.ForMachine(MachineTwo);
        var occurrence = CreateOccurrence("SHIFT-A", "SCHEDULE-A", 6, 14);
        var day = new ProductionDayId(new SiteId("SITE-1"), new DateOnly(2026, 8, 27));
        var input = CreateInput(otherStream, 1, "FACT-1", "running-duration", 10m, "seconds", occurrence, day, 7, 0, MachineTwo);

        Assert.Throws<ArgumentException>(() =>
            MetricInputContributionAggregator.Aggregate(stream, [input]));
    }

    [Fact]
    public void OvernightInputUsesAssignedProductionDayWithoutCalendarRecomputation()
    {
        var stream = MetricInputStreamId.ForMachine(MachineOne);
        var occurrence = new ShiftOccurrenceId(
            new SiteId("SITE-1"),
            new ShiftScheduleAssignmentId("SCHEDULE-NIGHT"),
            new ShiftId("SHIFT-NIGHT"),
            new DateTimeOffset(2026, 8, 27, 18, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 28, 2, 30, 0, TimeSpan.Zero));
        var assignedDay = new ProductionDayId(new SiteId("SITE-1"), new DateOnly(2026, 8, 27));
        var input = CreateInput(stream, 1, "FACT-NIGHT", "running-duration", 60m, "seconds", occurrence, assignedDay, 1, 0, occurredOnNextUtcDay: true);

        var result = MetricInputContributionAggregator.Aggregate(stream, [input]);

        var productionDay = Assert.Single(result.ProductionDayContributions);
        Assert.Equal(new DateOnly(2026, 8, 27), productionDay.Key.ProductionDayId.BusinessDate);
    }

    [Fact]
    public void DuplicatePositionsAndFactIdentitiesAreRejected()
    {
        var stream = MetricInputStreamId.ForMachine(MachineOne);
        var occurrence = CreateOccurrence("SHIFT-A", "SCHEDULE-A", 6, 14);
        var day = new ProductionDayId(new SiteId("SITE-1"), new DateOnly(2026, 8, 27));
        var first = CreateInput(stream, 1, "FACT-1", "running-duration", 10m, "seconds", occurrence, day, 7, 0);
        var duplicatePosition = CreateInput(stream, 1, "FACT-2", "running-duration", 20m, "seconds", occurrence, day, 8, 0);
        var duplicateId = CreateInput(stream, 2, "FACT-1", "running-duration", 20m, "seconds", occurrence, day, 8, 0);

        Assert.Throws<ArgumentException>(() =>
            MetricInputContributionAggregator.Aggregate(stream, [first, duplicatePosition]));
        Assert.Throws<ArgumentException>(() =>
            MetricInputContributionAggregator.Aggregate(stream, [first, duplicateId]));
    }

    private static ShiftOccurrenceId CreateOccurrence(
        string shiftId,
        string scheduleId,
        int startsHour,
        int endsHour,
        SiteId? siteId = null) =>
        new(
            siteId ?? new SiteId("SITE-1"),
            new ShiftScheduleAssignmentId(scheduleId),
            new ShiftId(shiftId),
            new DateTimeOffset(2026, 8, 27, startsHour, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 27, endsHour, 0, 0, TimeSpan.Zero));

    private static PositionedMetricInputFact CreateInput(
        MetricInputStreamId stream,
        ulong position,
        string factId,
        string key,
        decimal value,
        string unit,
        ShiftOccurrenceId occurrence,
        ProductionDayId productionDay,
        int hour,
        int minute,
        MachineId? machineId = null,
        bool occurredOnNextUtcDay = false)
    {
        var machine = machineId ?? MachineOne;
        var date = occurredOnNextUtcDay ? new DateOnly(2026, 8, 28) : new DateOnly(2026, 8, 27);
        var startsAt = new DateTimeOffset(date.Year, date.Month, date.Day, hour, minute, 0, TimeSpan.Zero);
        var fact = new DurableMetricInputFact
        {
            Id = new MetricInputFactId(factId),
            Key = key,
            Value = value,
            Unit = unit,
            StartsAtUtc = startsAt,
            EndsAtUtc = startsAt.AddMinutes(1),
            CompanyId = new CompanyId("COMP-1"),
            SiteId = occurrence.SiteId,
            ProductionLineId = new ProductionLineId("LINE-1"),
            MachineId = machine,
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
