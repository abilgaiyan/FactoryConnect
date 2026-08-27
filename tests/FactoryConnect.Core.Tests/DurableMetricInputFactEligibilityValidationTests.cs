using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;
using Xunit;

namespace FactoryConnect.Core.Tests;

public sealed class DurableMetricInputFactEligibilityValidationTests
{
    [Fact]
    public void SameMachineOverlapWithDifferentContextIsRejected()
    {
        var first = CreateEligibility("E1", 8, 2) with
        {
            ProductionContextAssignmentId = new ProductionContextAssignmentId("CTX-1"),
        };
        var second = CreateEligibility("E2", 9, 2) with
        {
            ProductionContextAssignmentId = new ProductionContextAssignmentId("CTX-2"),
        };

        Assert.Throws<InvalidOperationException>(() =>
            DurableMetricInputFactDeriver.Derive([first, second], []));
    }

    [Fact]
    public void SameMachineOverlapWithDifferentShiftIsRejected()
    {
        var first = CreateEligibility("E1", 8, 2) with { ShiftId = new ShiftId("SHIFT-1") };
        var second = CreateEligibility("E2", 9, 2) with { ShiftId = new ShiftId("SHIFT-2") };

        Assert.Throws<InvalidOperationException>(() =>
            DurableMetricInputFactDeriver.Derive([first, second], []));
    }

    [Fact]
    public void SameMachineOverlapWithDifferentOrderAndOperatorIsRejected()
    {
        var first = CreateEligibility("E1", 8, 2) with
        {
            ProductionOrderId = new ProductionOrderId("ORDER-1"),
            OperatorId = new OperatorId("OP-1"),
        };
        var second = CreateEligibility("E2", 9, 2) with
        {
            ProductionOrderId = new ProductionOrderId("ORDER-2"),
            OperatorId = new OperatorId("OP-2"),
        };

        Assert.Throws<InvalidOperationException>(() =>
            DurableMetricInputFactDeriver.Derive([first, second], []));
    }

    [Fact]
    public void DifferentMachinesMayOverlap()
    {
        var first = CreateEligibility("E1", 8, 2);
        var second = CreateEligibility("E2", 8, 2) with
        {
            MachineId = new MachineId(new Guid("22222222-2222-2222-2222-222222222222")),
        };

        var result = DurableMetricInputFactDeriver.Derive([first, second], []);

        Assert.NotEmpty(result);
    }

    [Fact]
    public void EmptyShiftScheduleAssignmentIdIsRejected()
    {
        var interval = CreateEligibility("E1", 8, 1) with
        {
            ShiftScheduleAssignmentId = new ShiftScheduleAssignmentId(" "),
        };

        Assert.Throws<ArgumentException>(() =>
            DurableMetricInputFactDeriver.Derive([interval], []));
    }

    [Fact]
    public void EmptySourceContextualizedActivityIntervalIdIsRejected()
    {
        var interval = CreateEligibility("E1", 8, 1) with
        {
            SourceContextualizedActivityIntervalId = new ContextualizedActivityIntervalId(" "),
        };

        Assert.Throws<ArgumentException>(() =>
            DurableMetricInputFactDeriver.Derive([interval], []));
    }

    [Fact]
    public void PlannedEligibilityRequiresScheduleAssignment()
    {
        var interval = CreateEligibility("E1", 8, 1) with
        {
            IsPlannedProductionTime = true,
            PlannedProductionScheduleAssignmentId = null,
        };

        Assert.Throws<ArgumentException>(() =>
            DurableMetricInputFactDeriver.Derive([interval], []));
    }

    [Fact]
    public void NonPlannedEligibilityMustNotReferenceScheduleAssignment()
    {
        var interval = CreateEligibility("E1", 8, 1) with
        {
            IsPlannedProductionTime = false,
            PlannedProductionScheduleAssignmentId = new PlannedProductionScheduleAssignmentId("POT-1"),
        };

        Assert.Throws<ArgumentException>(() =>
            DurableMetricInputFactDeriver.Derive([interval], []));
    }

    [Fact]
    public void PresentButEmptyOptionalContextIdentifierIsRejected()
    {
        var interval = CreateEligibility("E1", 8, 1) with
        {
            ProductionOrderId = new ProductionOrderId(" "),
        };

        Assert.Throws<ArgumentException>(() =>
            DurableMetricInputFactDeriver.Derive([interval], []));
    }

    private static ProductionTimeEligibilityInterval CreateEligibility(
        string id,
        int startHour,
        int durationHours)
    {
        var startsAt = new DateTimeOffset(2026, 8, 26, startHour, 0, 0, TimeSpan.Zero);
        return new ProductionTimeEligibilityInterval
        {
            Id = new ProductionTimeEligibilityIntervalId(id),
            SourceContextualizedActivityIntervalId = new ContextualizedActivityIntervalId($"CTX-{id}"),
            CompanyId = new CompanyId("COMP-1"),
            SiteId = new SiteId("SITE-1"),
            ProductionLineId = new ProductionLineId("LINE-1"),
            MachineId = new MachineId(new Guid("11111111-1111-1111-1111-111111111111")),
            State = MachineState.Running,
            ShiftId = new ShiftId("SHIFT-1"),
            ShiftScheduleAssignmentId = new ShiftScheduleAssignmentId("SHIFT-SCHEDULE-1"),
            ProductionContextAssignmentId = new ProductionContextAssignmentId("CONTEXT-1"),
            ProductionOrderId = new ProductionOrderId("ORDER-1"),
            OperationId = new OperationId("OP-10"),
            PartId = new PartId("PART-1"),
            OperatorId = new OperatorId("OPERATOR-1"),
            StartsAtUtc = startsAt,
            EndsAtUtc = startsAt.AddHours(durationHours),
            IsPlannedProductionTime = true,
            PlannedProductionScheduleAssignmentId = new PlannedProductionScheduleAssignmentId("POT-1"),
        };
    }
}
