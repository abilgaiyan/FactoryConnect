using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using Xunit;

namespace FactoryConnect.Core.Tests;

public sealed class ProductionContextValidationRegressionTests
{
    [Fact]
    public void ReaderRejectsDuplicateAssignmentIdForSameMachineWithAdjacentIntervals()
    {
        var machineId = MachineId.New();
        var start = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
        var reader = new InMemoryProductionContextReader([
            CreateAssignment("A1", machineId, start, start.AddHours(2)),
        ]);

        Assert.Throws<InvalidOperationException>(() =>
            reader.Add(CreateAssignment("A1", machineId, start.AddHours(2), start.AddHours(4))));
    }

    [Fact]
    public void ReaderRejectsDuplicateAssignmentIdAcrossDifferentMachines()
    {
        var start = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
        var reader = new InMemoryProductionContextReader([
            CreateAssignment("A1", MachineId.New(), start, start.AddHours(2)),
        ]);

        Assert.Throws<InvalidOperationException>(() =>
            reader.Add(CreateAssignment("A1", MachineId.New(), start.AddHours(2), start.AddHours(4))));
    }

    [Fact]
    public async Task RejectedDuplicateAssignmentDoesNotAlterExistingHistory()
    {
        var machineId = MachineId.New();
        var otherMachineId = MachineId.New();
        var start = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
        var original = CreateAssignment("A1", machineId, start, start.AddHours(2));
        var reader = new InMemoryProductionContextReader([original]);

        Assert.Throws<InvalidOperationException>(() =>
            reader.Add(CreateAssignment("A1", otherMachineId, start.AddHours(2), start.AddHours(4))));

        var originalHistory = await reader.ReadAsync(
            machineId,
            start,
            start.AddHours(4),
            CancellationToken.None);
        var otherHistory = await reader.ReadAsync(
            otherMachineId,
            start,
            start.AddHours(4),
            CancellationToken.None);

        Assert.Single(originalHistory);
        Assert.Equal(original, originalHistory[0]);
        Assert.Empty(otherHistory);
    }

    [Fact]
    public void AssignmentRejectsEmptyProductionOrderIdWhenSpecified()
    {
        var assignment = CreateAssignment("A1", MachineId.New(), DateTimeOffset.UtcNow, null) with
        {
            ProductionOrderId = new ProductionOrderId(" "),
        };

        Assert.Throws<ArgumentException>(assignment.Validate);
    }

    [Fact]
    public void AssignmentRejectsEmptyOperationIdWhenSpecified()
    {
        var assignment = CreateAssignment("A1", MachineId.New(), DateTimeOffset.UtcNow, null) with
        {
            OperationId = new OperationId(string.Empty),
        };

        Assert.Throws<ArgumentException>(assignment.Validate);
    }

    [Fact]
    public void AssignmentRejectsEmptyPartIdWhenSpecified()
    {
        var assignment = CreateAssignment("A1", MachineId.New(), DateTimeOffset.UtcNow, null) with
        {
            PartId = new PartId(" "),
        };

        Assert.Throws<ArgumentException>(assignment.Validate);
    }

    [Fact]
    public void AssignmentRejectsEmptyOperatorIdWhenSpecified()
    {
        var assignment = CreateAssignment("A1", MachineId.New(), DateTimeOffset.UtcNow, null) with
        {
            OperatorId = new OperatorId(string.Empty),
        };

        Assert.Throws<ArgumentException>(assignment.Validate);
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
