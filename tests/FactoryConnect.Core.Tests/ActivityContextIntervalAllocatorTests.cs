using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using Xunit;

namespace FactoryConnect.Core.Tests;

public sealed class ActivityContextIntervalAllocatorTests
{
    [Fact]
    public void AllocatorReturnsSingleIntervalWhenNoBoundaryOccurs()
    {
        var machineId = MachineId.New();
        var start = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
        var source = CreateSource(machineId, start, start.AddHours(2));
        var shift = CreateShift("S1", start.AddHours(-2), start.AddHours(6));
        var context = CreateContext("C1", machineId, start.AddHours(-1), start.AddHours(3));

        var result = new ActivityContextIntervalAllocator().Allocate(source, [shift], [context]);

        var interval = Assert.Single(result);
        Assert.Equal(start, interval.StartsAtUtc);
        Assert.Equal(start.AddHours(2), interval.EndsAtUtc);
        Assert.Equal(context.Id, interval.ProductionContextAssignmentId);
        Assert.Equal(shift.SourceAssignmentId, interval.ShiftScheduleAssignmentId);
        Assert.Equal(source.Position, interval.SourcePosition);
    }

    [Fact]
    public void AllocatorSplitsAtProductionContextBoundary()
    {
        var machineId = MachineId.New();
        var start = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
        var source = CreateSource(machineId, start, start.AddHours(6));
        var shift = CreateShift("S1", start, start.AddHours(6));
        var first = CreateContext("C1", machineId, start, start.AddHours(3));
        var second = CreateContext("C2", machineId, start.AddHours(3), start.AddHours(6));

        var result = new ActivityContextIntervalAllocator().Allocate(source, [shift], [first, second]);

        Assert.Equal(2, result.Count);
        Assert.Equal(start.AddHours(3), result[0].EndsAtUtc);
        Assert.Equal(first.Id, result[0].ProductionContextAssignmentId);
        Assert.Equal(start.AddHours(3), result[1].StartsAtUtc);
        Assert.Equal(second.Id, result[1].ProductionContextAssignmentId);
    }

    [Fact]
    public void AllocatorSplitsAtShiftBoundary()
    {
        var machineId = MachineId.New();
        var start = new DateTimeOffset(2026, 8, 26, 13, 0, 0, TimeSpan.Zero);
        var source = CreateSource(machineId, start, start.AddHours(2));
        var first = CreateShift("S1", start.AddHours(-7), start.AddHours(1));
        var second = CreateShift("S2", start.AddHours(1), start.AddHours(9));
        var context = CreateContext("C1", machineId, start.AddHours(-1), start.AddHours(3));

        var result = new ActivityContextIntervalAllocator().Allocate(source, [first, second], [context]);

        Assert.Equal(2, result.Count);
        Assert.Equal(first.ShiftId, result[0].ShiftId);
        Assert.Equal(start.AddHours(1), result[0].EndsAtUtc);
        Assert.Equal(second.ShiftId, result[1].ShiftId);
        Assert.Equal(start.AddHours(1), result[1].StartsAtUtc);
    }

    [Fact]
    public void AllocatorSplitsAtBothShiftAndContextBoundaries()
    {
        var machineId = MachineId.New();
        var start = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
        var source = CreateSource(machineId, start, start.AddHours(8));
        var firstShift = CreateShift("S1", start.AddHours(-2), start.AddHours(6));
        var secondShift = CreateShift("S2", start.AddHours(6), start.AddHours(14));
        var firstContext = CreateContext("C1", machineId, start, start.AddHours(3));
        var secondContext = CreateContext("C2", machineId, start.AddHours(3), start.AddHours(8));

        var result = new ActivityContextIntervalAllocator().Allocate(
            source,
            [firstShift, secondShift],
            [firstContext, secondContext]);

        Assert.Equal(3, result.Count);
        Assert.Equal((start, start.AddHours(3)), (result[0].StartsAtUtc, result[0].EndsAtUtc));
        Assert.Equal((start.AddHours(3), start.AddHours(6)), (result[1].StartsAtUtc, result[1].EndsAtUtc));
        Assert.Equal((start.AddHours(6), start.AddHours(8)), (result[2].StartsAtUtc, result[2].EndsAtUtc));
    }

    [Fact]
    public void AllocatorPreservesMissingContextWithoutLosingActivity()
    {
        var machineId = MachineId.New();
        var start = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
        var source = CreateSource(machineId, start, start.AddHours(4));
        var shift = CreateShift("S1", start, start.AddHours(4));
        var context = CreateContext("C1", machineId, start.AddHours(2), start.AddHours(4));

        var result = new ActivityContextIntervalAllocator().Allocate(source, [shift], [context]);

        Assert.Equal(2, result.Count);
        Assert.Null(result[0].ProductionContextAssignmentId);
        Assert.Equal(context.Id, result[1].ProductionContextAssignmentId);
        Assert.Equal(source.Period.Duration, result.Aggregate(TimeSpan.Zero, static (total, item) => total + item.Duration));
    }

    [Fact]
    public void AllocatorRejectsGapInShiftCoverage()
    {
        var machineId = MachineId.New();
        var start = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
        var source = CreateSource(machineId, start, start.AddHours(4));
        var first = CreateShift("S1", start, start.AddHours(1));
        var second = CreateShift("S2", start.AddHours(2), start.AddHours(4));

        Assert.Throws<InvalidOperationException>(() =>
            new ActivityContextIntervalAllocator().Allocate(source, [first, second], []));
    }

    [Fact]
    public void AllocatorRejectsOverlappingShiftCoverage()
    {
        var machineId = MachineId.New();
        var start = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
        var source = CreateSource(machineId, start, start.AddHours(4));
        var first = CreateShift("S1", start, start.AddHours(3));
        var second = CreateShift("S2", start.AddHours(2), start.AddHours(4));

        Assert.Throws<InvalidOperationException>(() =>
            new ActivityContextIntervalAllocator().Allocate(source, [first, second], []));
    }

    [Fact]
    public void AllocatorProducesDeterministicIdsAcrossReplay()
    {
        var machineId = MachineId.New();
        var start = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
        var source = CreateSource(machineId, start, start.AddHours(4));
        var shift = CreateShift("S1", start, start.AddHours(4));
        var firstContext = CreateContext("C1", machineId, start, start.AddHours(2));
        var secondContext = CreateContext("C2", machineId, start.AddHours(2), start.AddHours(4));
        var allocator = new ActivityContextIntervalAllocator();

        var first = allocator.Allocate(source, [shift], [firstContext, secondContext]);
        var second = allocator.Allocate(source, [shift], [firstContext, secondContext]);

        Assert.Equal(first.Select(static item => item.Id), second.Select(static item => item.Id));
        Assert.Equal(source.Period.Duration, first.Aggregate(TimeSpan.Zero, static (total, item) => total + item.Duration));
    }

    private static DurableMachineActivityPeriod CreateSource(
        MachineId machineId,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt) =>
        new(
            new ObservationProcessorId("activity-projection"),
            new ObservationPosition(10),
            new ObservationStreamId(machineId, "stream-1"),
            1,
            25,
            new MachineActivityPeriod(machineId, MachineState.Running, startsAt, endsAt));

    private static ShiftOccurrence CreateShift(
        string id,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt) =>
        new()
        {
            SourceAssignmentId = new ShiftScheduleAssignmentId(id),
            ShiftId = new ShiftId(id),
            SiteId = new SiteId("SITE-1"),
            FactoryDate = DateOnly.FromDateTime(startsAt.UtcDateTime),
            StartsAtUtc = startsAt,
            EndsAtUtc = endsAt,
        };

    private static ProductionContextAssignment CreateContext(
        string id,
        MachineId machineId,
        DateTimeOffset effectiveFrom,
        DateTimeOffset? effectiveTo) =>
        new()
        {
            Id = new ProductionContextAssignmentId(id),
            CompanyId = new CompanyId("COMP-1"),
            SiteId = new SiteId("SITE-1"),
            ProductionLineId = new ProductionLineId("LINE-1"),
            MachineId = machineId,
            PartId = new PartId($"PART-{id}"),
            EffectiveFrom = effectiveFrom,
            EffectiveTo = effectiveTo,
        };
}
