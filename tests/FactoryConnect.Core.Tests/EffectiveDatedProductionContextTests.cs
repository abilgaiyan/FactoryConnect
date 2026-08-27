using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using Xunit;

namespace FactoryConnect.Core.Tests;

public sealed class EffectiveDatedProductionContextTests
{
    [Fact]
    public void AssignmentSupportsFullyPopulatedContext()
    {
        var assignment = CreateAssignment(
            "A1",
            MachineId.New(),
            new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero)) with
        {
            ProductionOrderId = new ProductionOrderId("PO-1"),
            OperationId = new OperationId("OP-10"),
            PartId = new PartId("PART-1"),
            OperatorId = new OperatorId("OPER-1"),
        };

        assignment.Validate();

        Assert.Equal("PO-1", assignment.ProductionOrderId?.Value);
        Assert.Equal("OP-10", assignment.OperationId?.Value);
        Assert.Equal("PART-1", assignment.PartId?.Value);
        Assert.Equal("OPER-1", assignment.OperatorId?.Value);
    }

    [Fact]
    public void AssignmentAllowsOptionalProductionReferences()
    {
        var assignment = CreateAssignment(
            "A1",
            MachineId.New(),
            DateTimeOffset.UtcNow,
            null);

        assignment.Validate();

        Assert.Null(assignment.ProductionOrderId);
        Assert.Null(assignment.OperationId);
        Assert.Null(assignment.PartId);
        Assert.Null(assignment.OperatorId);
    }

    [Fact]
    public void AssignmentSupportsOpenEndedInterval()
    {
        var start = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
        var assignment = CreateAssignment("A1", MachineId.New(), start, null);

        Assert.True(assignment.Contains(start));
        Assert.True(assignment.Contains(start.AddYears(1)));
    }

    [Fact]
    public void AssignmentRejectsEqualEffectiveBoundaries()
    {
        var start = DateTimeOffset.UtcNow;
        var assignment = CreateAssignment("A1", MachineId.New(), start, start);

        Assert.Throws<ArgumentException>(assignment.Validate);
    }

    [Fact]
    public void AssignmentRejectsEffectiveToBeforeEffectiveFrom()
    {
        var start = DateTimeOffset.UtcNow;
        var assignment = CreateAssignment("A1", MachineId.New(), start, start.AddTicks(-1));

        Assert.Throws<ArgumentException>(assignment.Validate);
    }

    [Fact]
    public void ContainsUsesInclusiveStartAndExclusiveEnd()
    {
        var start = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
        var end = start.AddHours(4);
        var assignment = CreateAssignment("A1", MachineId.New(), start, end);

        Assert.True(assignment.Contains(start));
        Assert.True(assignment.Contains(end.AddTicks(-1)));
        Assert.False(assignment.Contains(end));
    }

    [Fact]
    public async Task ReaderReturnsAssignmentsIntersectingRequestedIntervalInDeterministicOrder()
    {
        var machineId = MachineId.New();
        var start = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
        var first = CreateAssignment("A1", machineId, start, start.AddHours(4));
        var second = CreateAssignment("A2", machineId, start.AddHours(4), start.AddHours(8));
        var reader = new InMemoryProductionContextReader([second, first]);

        var result = await reader.ReadAsync(
            machineId,
            start.AddHours(2),
            start.AddHours(6),
            CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal(first.Id, result[0].Id);
        Assert.Equal(second.Id, result[1].Id);
    }

    [Fact]
    public void ReaderRejectsOverlappingAssignmentsForSameMachine()
    {
        var machineId = MachineId.New();
        var start = DateTimeOffset.UtcNow;
        var reader = new InMemoryProductionContextReader([
            CreateAssignment("A1", machineId, start, start.AddHours(4)),
        ]);

        Assert.Throws<InvalidOperationException>(() =>
            reader.Add(CreateAssignment("A2", machineId, start.AddHours(3), start.AddHours(5))));
    }

    [Fact]
    public void ReaderAllowsOverlappingAssignmentsForDifferentMachines()
    {
        var start = DateTimeOffset.UtcNow;
        var reader = new InMemoryProductionContextReader([
            CreateAssignment("A1", MachineId.New(), start, start.AddHours(4)),
        ]);

        reader.Add(CreateAssignment("A2", MachineId.New(), start.AddHours(1), start.AddHours(5)));
    }

    [Fact]
    public async Task ResolverPreservesHistoricalAssignmentWhenLaterAssignmentIsAdded()
    {
        var machineId = MachineId.New();
        var start = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
        var first = CreateAssignment("A1", machineId, start, start.AddHours(3)) with
        {
            PartId = new PartId("PART-A"),
        };
        var second = CreateAssignment("A2", machineId, start.AddHours(3), null) with
        {
            PartId = new PartId("PART-B"),
        };
        var reader = new InMemoryProductionContextReader([first]);
        reader.Add(second);
        var resolver = new ProductionContextResolver(reader);

        var historical = await resolver.ResolveAtAsync(
            machineId,
            start.AddHours(2),
            CancellationToken.None);
        var current = await resolver.ResolveAtAsync(
            machineId,
            start.AddHours(3),
            CancellationToken.None);

        Assert.Equal("PART-A", historical?.PartId?.Value);
        Assert.Equal("PART-B", current?.PartId?.Value);
    }

    [Fact]
    public async Task ReaderPropagatesCancellation()
    {
        var reader = new InMemoryProductionContextReader();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var from = DateTimeOffset.UtcNow;

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            reader.ReadAsync(
                MachineId.New(),
                from,
                from.AddHours(1),
                cancellation.Token));
    }

    private static ProductionContextAssignment CreateAssignment(
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
            EffectiveFrom = effectiveFrom,
            EffectiveTo = effectiveTo,
        };
}
