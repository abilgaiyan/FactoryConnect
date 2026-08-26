using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;
using Xunit;

namespace FactoryConnect.Core.Tests;

public sealed class DurableMetricInputFactDeriverHardeningTests
{
    [Fact]
    public void DurationFactsPreserveEligibilityAndScheduleLineage()
    {
        var interval = CreateEligibility("E1", MachineState.Running, true, 8, 9);

        var facts = DurableMetricInputFactDeriver.Derive([interval], []);

        Assert.All(facts, fact =>
        {
            Assert.Equal(interval.IsPlannedProductionTime, fact.IsPlannedProductionTime);
            Assert.Equal(interval.PlannedProductionScheduleAssignmentId, fact.PlannedProductionScheduleAssignmentId);
            Assert.Equal(interval.ShiftScheduleAssignmentId, fact.ShiftScheduleAssignmentId);
            Assert.Equal(interval.SourceContextualizedActivityIntervalId, fact.SourceContextualizedActivityIntervalId);
            Assert.Equal(interval.Id, fact.SourceEligibilityIntervalId);
        });
    }

    [Fact]
    public void NonPlannedStateFactRemainsExplicitlyNonPlanned()
    {
        var interval = CreateEligibility("E1", MachineState.Running, false, 8, 9);

        var fact = DurableMetricInputFactDeriver.Derive([interval], [])
            .Single(item => item.Key == MetricInputFactKeys.RunningDuration);

        Assert.False(fact.IsPlannedProductionTime);
        Assert.Null(fact.PlannedProductionScheduleAssignmentId);
    }

    [Fact]
    public void OfflineStateDerivesOfflineDuration()
    {
        var interval = CreateEligibility("E1", MachineState.Offline, true, 8, 9);

        var facts = DurableMetricInputFactDeriver.Derive([interval], []);

        Assert.Contains(facts, fact => fact.Key == MetricInputFactKeys.OfflineDuration && fact.Value == 3600m);
    }

    [Fact]
    public void UnknownStateDoesNotDeriveOfflineDuration()
    {
        var interval = CreateEligibility("E1", MachineState.Unknown, true, 8, 9);

        var facts = DurableMetricInputFactDeriver.Derive([interval], []);

        Assert.DoesNotContain(facts, fact => fact.Key == MetricInputFactKeys.OfflineDuration);
    }

    [Fact]
    public void DuplicateEligibilityIdIsRejected()
    {
        var first = CreateEligibility("E1", MachineState.Running, true, 8, 9);
        var duplicate = CreateEligibility("E1", MachineState.Idle, true, 9, 10);

        Assert.Throws<InvalidOperationException>(() =>
            DurableMetricInputFactDeriver.Derive([first, duplicate], []));
    }

    [Fact]
    public void DuplicateQuantityEvidenceIdIsRejected()
    {
        var first = CreateQuantity("Q1", 1);
        var duplicate = CreateQuantity("Q1", 2);

        Assert.Throws<InvalidOperationException>(() =>
            DurableMetricInputFactDeriver.Derive([], [first, duplicate]));
    }

    [Fact]
    public void OverlappingEligibilityWithinSameMetricScopeIsRejected()
    {
        var first = CreateEligibility("E1", MachineState.Running, true, 8, 10);
        var second = CreateEligibility("E2", MachineState.Idle, true, 9, 11);

        Assert.Throws<InvalidOperationException>(() =>
            DurableMetricInputFactDeriver.Derive([first, second], []));
    }

    [Fact]
    public void AdjacentEligibilityWithinSameMetricScopeIsAllowed()
    {
        var first = CreateEligibility("E1", MachineState.Running, true, 8, 9);
        var second = CreateEligibility("E2", MachineState.Idle, true, 9, 10);

        var facts = DurableMetricInputFactDeriver.Derive([second, first], []);

        Assert.NotEmpty(facts);
    }

    [Fact]
    public void QuantityFactsPreserveHierarchyAndContext()
    {
        var evidence = CreateQuantity("Q1", 3);

        var fact = DurableMetricInputFactDeriver.Derive([], [evidence])
            .Single(item => item.Key == MetricInputFactKeys.PartCountIncrement);

        Assert.Equal(evidence.CompanyId, fact.CompanyId);
        Assert.Equal(evidence.SiteId, fact.SiteId);
        Assert.Equal(evidence.ProductionLineId, fact.ProductionLineId);
        Assert.Equal(evidence.MachineId, fact.MachineId);
        Assert.Equal(evidence.ShiftId, fact.ShiftId);
        Assert.Equal(evidence.ProductionContextAssignmentId, fact.ProductionContextAssignmentId);
        Assert.Equal(evidence.ProductionOrderId, fact.ProductionOrderId);
        Assert.Equal(evidence.OperationId, fact.OperationId);
        Assert.Equal(evidence.PartId, fact.PartId);
        Assert.Equal(evidence.OperatorId, fact.OperatorId);
        Assert.Equal(evidence.Id, fact.SourceQuantityEvidenceId);
    }

    [Fact]
    public void ZeroQuantityIsPreservedAsEvidence()
    {
        var evidence = CreateQuantity("Q1", 0);

        var fact = DurableMetricInputFactDeriver.Derive([], [evidence])
            .Single(item => item.Key == MetricInputFactKeys.PartCountIncrement);

        Assert.Equal(0m, fact.Value);
    }

    [Fact]
    public void NullSourceRecordsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() =>
            DurableMetricInputFactDeriver.Derive([null!], []));
        Assert.Throws<ArgumentNullException>(() =>
            DurableMetricInputFactDeriver.Derive([], [null!]));
    }

    [Fact]
    public void FractionalSecondDurationIsPreserved()
    {
        var interval = CreateEligibility("E1", MachineState.Running, true, 8, 9) with
        {
            EndsAtUtc = new DateTimeOffset(2026, 8, 26, 8, 0, 1, 500, TimeSpan.Zero),
        };

        var fact = DurableMetricInputFactDeriver.Derive([interval], [])
            .Single(item => item.Key == MetricInputFactKeys.RunningDuration);

        Assert.Equal(1.5m, fact.Value);
    }

    [Fact]
    public void DifferentMachinesMayOverlapWithoutConflict()
    {
        var first = CreateEligibility("E1", MachineState.Running, true, 8, 10);
        var second = CreateEligibility("E2", MachineState.Running, true, 8, 10) with
        {
            MachineId = new MachineId(new Guid("22222222-2222-2222-2222-222222222222")),
        };

        var facts = DurableMetricInputFactDeriver.Derive([first, second], []);

        Assert.Contains(facts, fact => fact.MachineId == first.MachineId);
        Assert.Contains(facts, fact => fact.MachineId == second.MachineId);
    }

    private static ProductionTimeEligibilityInterval CreateEligibility(
        string id,
        MachineState state,
        bool isPlanned,
        int startHour,
        int endHour)
    {
        var startsAt = new DateTimeOffset(2026, 8, 26, startHour, 0, 0, TimeSpan.Zero);
        var endsAt = new DateTimeOffset(2026, 8, 26, endHour, 0, 0, TimeSpan.Zero);
        return new ProductionTimeEligibilityInterval
        {
            Id = new ProductionTimeEligibilityIntervalId(id),
            SourceContextualizedActivityIntervalId = new ContextualizedActivityIntervalId($"CTX-{id}"),
            CompanyId = new CompanyId("COMP-1"),
            SiteId = new SiteId("SITE-1"),
            ProductionLineId = new ProductionLineId("LINE-1"),
            MachineId = new MachineId(new Guid("11111111-1111-1111-1111-111111111111")),
            State = state,
            ShiftId = new ShiftId("SHIFT-1"),
            ShiftScheduleAssignmentId = new ShiftScheduleAssignmentId("SHIFT-SCHEDULE-1"),
            ProductionContextAssignmentId = new ProductionContextAssignmentId("CONTEXT-1"),
            ProductionOrderId = new ProductionOrderId("ORDER-1"),
            OperationId = new OperationId("OP-10"),
            PartId = new PartId("PART-1"),
            OperatorId = new OperatorId("OPERATOR-1"),
            StartsAtUtc = startsAt,
            EndsAtUtc = endsAt,
            IsPlannedProductionTime = isPlanned,
            PlannedProductionScheduleAssignmentId = isPlanned
                ? new PlannedProductionScheduleAssignmentId("POT-1")
                : null,
        };
    }

    private static ProductionQuantityEvidence CreateQuantity(string id, int increment) =>
        new()
        {
            Id = new ProductionQuantityEvidenceId(id),
            CompanyId = new CompanyId("COMP-1"),
            SiteId = new SiteId("SITE-1"),
            ProductionLineId = new ProductionLineId("LINE-1"),
            MachineId = new MachineId(new Guid("11111111-1111-1111-1111-111111111111")),
            ShiftId = new ShiftId("SHIFT-1"),
            ProductionContextAssignmentId = new ProductionContextAssignmentId("CONTEXT-1"),
            ProductionOrderId = new ProductionOrderId("ORDER-1"),
            OperationId = new OperationId("OP-10"),
            PartId = new PartId("PART-1"),
            OperatorId = new OperatorId("OPERATOR-1"),
            OccurredAtUtc = new DateTimeOffset(2026, 8, 26, 8, 30, 0, TimeSpan.Zero),
            PartCountIncrement = increment,
        };
}
