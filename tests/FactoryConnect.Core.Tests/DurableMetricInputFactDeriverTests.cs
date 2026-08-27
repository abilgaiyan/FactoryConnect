using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;
using Xunit;

namespace FactoryConnect.Core.Tests;

public sealed class DurableMetricInputFactDeriverTests
{
    [Fact]
    public void PlannedRunningIntervalDerivesScheduledPlannedAndRunningFacts()
    {
        var interval = CreateEligibility(MachineState.Running, isPlanned: true);

        var result = DurableMetricInputFactDeriver.Derive([interval], []);

        Assert.Equal(3, result.Count);
        Assert.Contains(result, fact => fact.Key == MetricInputFactKeys.ScheduledDuration && fact.Value == 3600m);
        Assert.Contains(result, fact => fact.Key == MetricInputFactKeys.PlannedProductionDuration && fact.Value == 3600m);
        Assert.Contains(result, fact => fact.Key == MetricInputFactKeys.RunningDuration && fact.Value == 3600m);
        Assert.All(result, fact => Assert.Equal(MetricInputFactUnits.Seconds, fact.Unit));
    }

    [Fact]
    public void NonPlannedIdleIntervalDoesNotDerivePlannedProductionDuration()
    {
        var interval = CreateEligibility(MachineState.Idle, isPlanned: false);

        var result = DurableMetricInputFactDeriver.Derive([interval], []);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, fact => fact.Key == MetricInputFactKeys.ScheduledDuration);
        Assert.Contains(result, fact => fact.Key == MetricInputFactKeys.IdleDuration);
        Assert.DoesNotContain(result, fact => fact.Key == MetricInputFactKeys.PlannedProductionDuration);
    }

    [Theory]
    [InlineData(MachineState.Running, MetricInputFactKeys.RunningDuration)]
    [InlineData(MachineState.Idle, MetricInputFactKeys.IdleDuration)]
    [InlineData(MachineState.Stopped, MetricInputFactKeys.StoppedDuration)]
    [InlineData(MachineState.Fault, MetricInputFactKeys.AlarmDuration)]
    public void MachineStateDerivesExpectedDurationFact(MachineState state, string expectedKey)
    {
        var interval = CreateEligibility(state, isPlanned: true);

        var result = DurableMetricInputFactDeriver.Derive([interval], []);

        Assert.Contains(result, fact => fact.Key == expectedKey);
    }

    [Fact]
    public void UnknownStateDoesNotInventStateDurationFact()
    {
        var interval = CreateEligibility(MachineState.Unknown, isPlanned: true);

        var result = DurableMetricInputFactDeriver.Derive([interval], []);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, fact => fact.Key == MetricInputFactKeys.ScheduledDuration);
        Assert.Contains(result, fact => fact.Key == MetricInputFactKeys.PlannedProductionDuration);
    }

    [Fact]
    public void QuantityFactsRequireExplicitEvidence()
    {
        var interval = CreateEligibility(MachineState.Running, isPlanned: true);

        var result = DurableMetricInputFactDeriver.Derive([interval], []);

        Assert.DoesNotContain(result, fact => fact.Key == MetricInputFactKeys.PartCountIncrement);
        Assert.DoesNotContain(result, fact => fact.Key == MetricInputFactKeys.GoodQuantity);
        Assert.DoesNotContain(result, fact => fact.Key == MetricInputFactKeys.RejectedQuantity);
    }

    [Fact]
    public void ExplicitQuantityEvidenceDerivesOnlyAvailableQuantityFacts()
    {
        var evidence = CreateQuantityEvidence() with
        {
            PartCountIncrement = 2,
            GoodQuantity = 1,
            RejectedQuantity = null,
        };

        var result = DurableMetricInputFactDeriver.Derive([], [evidence]);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, fact => fact.Key == MetricInputFactKeys.PartCountIncrement && fact.Value == 2m);
        Assert.Contains(result, fact => fact.Key == MetricInputFactKeys.GoodQuantity && fact.Value == 1m);
        Assert.DoesNotContain(result, fact => fact.Key == MetricInputFactKeys.RejectedQuantity);
        Assert.All(result, fact => Assert.Equal(evidence.Id, fact.SourceQuantityEvidenceId));
    }

    [Fact]
    public void DerivationPreservesHierarchyContextAndLineage()
    {
        var interval = CreateEligibility(MachineState.Fault, isPlanned: true);

        var fact = DurableMetricInputFactDeriver.Derive([interval], [])
            .Single(item => item.Key == MetricInputFactKeys.AlarmDuration);

        Assert.Equal(interval.CompanyId, fact.CompanyId);
        Assert.Equal(interval.SiteId, fact.SiteId);
        Assert.Equal(interval.ProductionLineId, fact.ProductionLineId);
        Assert.Equal(interval.MachineId, fact.MachineId);
        Assert.Equal(interval.ShiftId, fact.ShiftId);
        Assert.Equal(interval.ProductionContextAssignmentId, fact.ProductionContextAssignmentId);
        Assert.Equal(interval.ProductionOrderId, fact.ProductionOrderId);
        Assert.Equal(interval.OperationId, fact.OperationId);
        Assert.Equal(interval.PartId, fact.PartId);
        Assert.Equal(interval.OperatorId, fact.OperatorId);
        Assert.Equal(interval.Id, fact.SourceEligibilityIntervalId);
    }

    [Fact]
    public void ReplayAndInputOrderingProduceSameFactsAndIds()
    {
        var first = CreateEligibility(MachineState.Running, isPlanned: true, id: "E1", hour: 8);
        var second = CreateEligibility(MachineState.Idle, isPlanned: false, id: "E2", hour: 9);

        var forward = DurableMetricInputFactDeriver.Derive([first, second], []);
        var reverse = DurableMetricInputFactDeriver.Derive([second, first], []);

        Assert.Equal(
            forward.Select(static fact => (fact.Id, fact.Key, fact.Value)),
            reverse.Select(static fact => (fact.Id, fact.Key, fact.Value)));
    }

    private static ProductionTimeEligibilityInterval CreateEligibility(
        MachineState state,
        bool isPlanned,
        string id = "ELIGIBILITY-1",
        int hour = 8)
    {
        var startsAt = new DateTimeOffset(2026, 8, 26, hour, 0, 0, TimeSpan.Zero);
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
            EndsAtUtc = startsAt.AddHours(1),
            IsPlannedProductionTime = isPlanned,
            PlannedProductionScheduleAssignmentId = isPlanned
                ? new PlannedProductionScheduleAssignmentId("POT-1")
                : null,
        };
    }

    private static ProductionQuantityEvidence CreateQuantityEvidence() =>
        new()
        {
            Id = new ProductionQuantityEvidenceId("Q1"),
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
            PartCountIncrement = 1,
        };
}
