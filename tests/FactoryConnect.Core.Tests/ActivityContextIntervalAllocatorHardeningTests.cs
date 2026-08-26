using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using Xunit;

namespace FactoryConnect.Core.Tests;

public sealed class ActivityContextIntervalAllocatorHardeningTests
{
    [Fact]
    public void AllocatorPreservesProductionHierarchyAndSourceLineageAcrossSplits()
    {
        var machineId = MachineId.New();
        var start = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
        var source = CreateSource(machineId, start, start.AddHours(4));
        var shift = CreateShift("SHIFT", start, start.AddHours(4), new ProductionLineId("LINE-1"));
        var first = CreateContext("C1", machineId, start, start.AddHours(2));
        var second = CreateContext("C2", machineId, start.AddHours(2), start.AddHours(4));

        var result = ActivityContextIntervalAllocator.Allocate(source, [shift], [first, second]);

        Assert.Equal(2, result.Count);
        Assert.All(result, interval =>
        {
            Assert.Equal(new CompanyId("COMP-1"), interval.CompanyId);
            Assert.Equal(new SiteId("SITE-1"), interval.SiteId);
            Assert.Equal(new ProductionLineId("LINE-1"), interval.ProductionLineId);
            Assert.Equal(source.ProcessorId, interval.SourceProcessorId);
            Assert.Equal(source.Position, interval.SourcePosition);
            Assert.Equal(source.StreamId, interval.SourceStreamId);
            Assert.Equal(source.InstanceId, interval.SourceInstanceId);
            Assert.Equal(source.Sequence, interval.SourceSequence);
            Assert.Equal(source.Period.MachineId, interval.MachineId);
            Assert.Equal(source.Period.State, interval.State);
        });
    }

    [Fact]
    public void AllocatorUsesShiftHierarchyWhenProductionContextIsMissing()
    {
        var machineId = MachineId.New();
        var start = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
        var source = CreateSource(machineId, start, start.AddHours(2));
        var shift = CreateShift("SHIFT", start, start.AddHours(2), new ProductionLineId("LINE-1"));

        var result = ActivityContextIntervalAllocator.Allocate(source, [shift], []);

        var interval = Assert.Single(result);
        Assert.Equal(shift.CompanyId, interval.CompanyId);
        Assert.Equal(shift.SiteId, interval.SiteId);
        Assert.Equal(shift.ProductionLineId, interval.ProductionLineId);
        Assert.Null(interval.ProductionContextAssignmentId);
    }

    [Fact]
    public void AllocatorRejectsDifferentSiteShiftAndContext()
    {
        var machineId = MachineId.New();
        var start = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
        var source = CreateSource(machineId, start, start.AddHours(2));
        var shift = CreateShift("SHIFT", start, start.AddHours(2), new ProductionLineId("LINE-1"));
        var context = CreateContext("CTX", machineId, start, start.AddHours(2)) with
        {
            SiteId = new SiteId("SITE-2"),
        };

        Assert.Throws<InvalidOperationException>(() =>
            ActivityContextIntervalAllocator.Allocate(source, [shift], [context]));
    }

    [Fact]
    public void AllocatorRejectsDifferentLineShiftAndContext()
    {
        var machineId = MachineId.New();
        var start = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
        var source = CreateSource(machineId, start, start.AddHours(2));
        var shift = CreateShift("SHIFT", start, start.AddHours(2), new ProductionLineId("LINE-2"));
        var context = CreateContext("CTX", machineId, start, start.AddHours(2));

        Assert.Throws<InvalidOperationException>(() =>
            ActivityContextIntervalAllocator.Allocate(source, [shift], [context]));
    }

    [Fact]
    public void AllocatorAllowsSiteWideShiftForLineSpecificContext()
    {
        var machineId = MachineId.New();
        var start = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
        var source = CreateSource(machineId, start, start.AddHours(2));
        var siteWideShift = CreateShift("SHIFT", start, start.AddHours(2), null);
        var context = CreateContext("CTX", machineId, start, start.AddHours(2));

        var result = ActivityContextIntervalAllocator.Allocate(source, [siteWideShift], [context]);

        var interval = Assert.Single(result);
        Assert.Equal(context.ProductionLineId, interval.ProductionLineId);
        Assert.Equal(context.SiteId, interval.SiteId);
        Assert.Equal(context.CompanyId, interval.CompanyId);
    }

    [Fact]
    public void AllocatorRejectsContextForAnotherMachine()
    {
        var machineId = MachineId.New();
        var start = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
        var source = CreateSource(machineId, start, start.AddHours(2));
        var shift = CreateShift("SHIFT", start, start.AddHours(2), null);
        var otherMachineContext = CreateContext("CTX", MachineId.New(), start, start.AddHours(2));

        Assert.Throws<InvalidOperationException>(() =>
            ActivityContextIntervalAllocator.Allocate(source, [shift], [otherMachineContext]));
    }

    [Fact]
    public void AllocatorSupportsOpenEndedContextAndContextGap()
    {
        var machineId = MachineId.New();
        var start = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
        var source = CreateSource(machineId, start, start.AddHours(6));
        var shift = CreateShift("SHIFT", start, start.AddHours(6), null);
        var first = CreateContext("C1", machineId, start, start.AddHours(2));
        var second = CreateContext("C2", machineId, start.AddHours(4), null);

        var result = ActivityContextIntervalAllocator.Allocate(source, [shift], [second, first]);

        Assert.Equal(3, result.Count);
        Assert.Equal(first.Id, result[0].ProductionContextAssignmentId);
        Assert.Null(result[1].ProductionContextAssignmentId);
        Assert.Equal(second.Id, result[2].ProductionContextAssignmentId);
        Assert.Equal((start.AddHours(2), start.AddHours(4)), (result[1].StartsAtUtc, result[1].EndsAtUtc));
    }

    [Fact]
    public void AllocatorInputOrderDoesNotAffectOutputOrIds()
    {
        var machineId = MachineId.New();
        var start = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
        var source = CreateSource(machineId, start, start.AddHours(4));
        var firstShift = CreateShift("S1", start, start.AddHours(2), null);
        var secondShift = CreateShift("S2", start.AddHours(2), start.AddHours(4), null);
        var firstContext = CreateContext("C1", machineId, start, start.AddHours(1));
        var secondContext = CreateContext("C2", machineId, start.AddHours(1), start.AddHours(4));

        var first = ActivityContextIntervalAllocator.Allocate(
            source,
            [firstShift, secondShift],
            [firstContext, secondContext]);
        var second = ActivityContextIntervalAllocator.Allocate(
            source,
            [secondShift, firstShift],
            [secondContext, firstContext]);

        Assert.Equal(first.Select(static interval => interval.Id), second.Select(static interval => interval.Id));
        Assert.Equal(first.Select(static interval => interval.StartsAtUtc), second.Select(static interval => interval.StartsAtUtc));
    }

    [Fact]
    public void AllocatorHonorsExactHalfOpenBoundaries()
    {
        var machineId = MachineId.New();
        var start = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
        var boundary = start.AddHours(2);
        var source = CreateSource(machineId, start, start.AddHours(4));
        var firstShift = CreateShift("S1", start, boundary, null);
        var secondShift = CreateShift("S2", boundary, start.AddHours(4), null);
        var firstContext = CreateContext("C1", machineId, start, boundary);
        var secondContext = CreateContext("C2", machineId, boundary, start.AddHours(4));

        var result = ActivityContextIntervalAllocator.Allocate(
            source,
            [secondShift, firstShift],
            [secondContext, firstContext]);

        Assert.Equal(2, result.Count);
        Assert.Equal((start, boundary), (result[0].StartsAtUtc, result[0].EndsAtUtc));
        Assert.Equal((boundary, start.AddHours(4)), (result[1].StartsAtUtc, result[1].EndsAtUtc));
        Assert.Equal(firstContext.Id, result[0].ProductionContextAssignmentId);
        Assert.Equal(secondContext.Id, result[1].ProductionContextAssignmentId);
    }

    [Fact]
    public void DeterministicIdEncodingIsSeparatorSafe()
    {
        var machineId = MachineId.New();
        var start = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
        var source = CreateSource(machineId, start, start.AddHours(1));
        var firstShift = CreateShift("A|B", start, start.AddHours(1), null);
        var secondShift = CreateShift("A", start, start.AddHours(1), null);
        var firstContext = CreateContext("C", machineId, start, start.AddHours(1));
        var secondContext = CreateContext("B|C", machineId, start, start.AddHours(1));

        var first = Assert.Single(ActivityContextIntervalAllocator.Allocate(source, [firstShift], [firstContext]));
        var second = Assert.Single(ActivityContextIntervalAllocator.Allocate(source, [secondShift], [secondContext]));

        Assert.NotEqual(first.Id, second.Id);
    }

    private static DurableMachineActivityPeriod CreateSource(
        MachineId machineId,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt) =>
        new(
            new ObservationProcessorId("activity|projection"),
            new ObservationPosition(10),
            new ObservationStreamId(machineId, "stream|1"),
            1,
            25,
            new MachineActivityPeriod(machineId, MachineState.Running, startsAt, endsAt));

    private static ShiftOccurrence CreateShift(
        string id,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        ProductionLineId? productionLineId) =>
        new()
        {
            SourceAssignmentId = new ShiftScheduleAssignmentId(id),
            CompanyId = new CompanyId("COMP-1"),
            ShiftId = new ShiftId(id),
            SiteId = new SiteId("SITE-1"),
            ProductionLineId = productionLineId,
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
            ProductionOrderId = new ProductionOrderId("PO-1"),
            OperationId = new OperationId("OP-10"),
            PartId = new PartId("PART-1"),
            OperatorId = new OperatorId("OPER-1"),
            EffectiveFrom = effectiveFrom,
            EffectiveTo = effectiveTo,
        };
}
