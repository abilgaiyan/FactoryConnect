using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Metrics;

/// <summary>Uses the very same historical contribution set as the snapshot aggregates.</summary>
public static class AvailabilityScopeConflictEvidenceBuilder
{
    public static AvailabilityScopeConflictEvidenceV1? Build(
        OperationalMetricComponentSnapshotRequest request,
        MetricAggregationCheckpoint revision,
        IReadOnlyList<PositionedMetricInputFact> committedContributions,
        IReadOnlyList<OperationalMetricComponent> components)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(committedContributions);
        ArgumentNullException.ThrowIfNull(components);
        if (!request.IncludeAvailabilityScopeConflictEvidence)
        {
            return null;
        }

        if (revision.ProcessorId != request.ProcessorId || revision.StreamId.MachineId != request.EvaluationKey.MachineId ||
            committedContributions.Any(input => input.StreamId != revision.StreamId || input.Position.Value == 0 || input.Position > revision.Position))
        {
            throw new InvalidDataException("Conflict evidence requires the exact committed processor/stream cut.");
        }

        // Validate duplicate identities/positions, even outside this period.
        _ = MetricInputContributionAggregator.Aggregate(revision.StreamId, committedContributions);
        var selected = committedContributions.Where(input => request.EvaluationKey.PeriodId switch
        {
            OperationalMetricPeriodId.Shift shift => input.ShiftOccurrenceId == shift.ShiftOccurrenceId,
            OperationalMetricPeriodId.ProductionDay day => input.ProductionDayId == day.ProductionDayId,
            _ => throw new InvalidDataException("Unsupported conflict evidence period."),
        }).ToArray();
        var numerator = components.SingleOrDefault(component => component.SourceIdentity.ComponentKey == MetricInputFactKeys.RunningDuration);
        var denominator = components.SingleOrDefault(component => component.SourceIdentity.ComponentKey == MetricInputFactKeys.PlannedProductionDuration);
        // Legacy component keys are not the declared eligibility-v1 contract.
        if (numerator is null || denominator is null)
        {
            return null;
        }

        ValidateAggregate(numerator, MetricInputFactKeys.RunningDuration);
        ValidateAggregate(denominator, MetricInputFactKeys.PlannedProductionDuration);
        var running = selected.Where(input => input.Fact.Key == MetricInputFactKeys.RunningDuration).ToArray();
        var planned = selected.Where(input => input.Fact.Key == MetricInputFactKeys.PlannedProductionDuration).ToArray();
        foreach (var input in running.Concat(planned))
        {
            var fact = input.Fact;
            if (fact.Unit != MetricInputFactUnits.Seconds || fact.EndsAtUtc <= fact.StartsAtUtc ||
                fact.StartsAtUtc.Offset != TimeSpan.Zero || fact.EndsAtUtc.Offset != TimeSpan.Zero ||
                fact.Value <= 0 || fact.Value != (decimal)(fact.EndsAtUtc - fact.StartsAtUtc).Ticks / TimeSpan.TicksPerSecond ||
                fact.SourceEligibilityIntervalId is null or { IsEmpty: true } || fact.SourceContextualizedActivityIntervalId is null or { IsEmpty: true } ||
                fact.IsPlannedProductionTime is null ||
                (fact.IsPlannedProductionTime == true) != (fact.PlannedProductionScheduleAssignmentId is not null) ||
                fact.PlannedProductionScheduleAssignmentId is { IsEmpty: true } ||
                (fact.Key == MetricInputFactKeys.PlannedProductionDuration && fact.IsPlannedProductionTime != true))
            {
                throw new InvalidDataException("Invalid duration or eligibility evidence in the operand contribution set.");
            }
        }

        var offending = running.Where(input => input.Fact.IsPlannedProductionTime == false)
            .OrderBy(input => input.Position.Value).FirstOrDefault();
        return offending is null ? null : new AvailabilityScopeConflictEvidenceV1(revision,
            new OperationalMetricComponent("ActualProductionTime", numerator.SourceIdentity, numerator.Dimension, numerator.Aggregate),
            new OperationalMetricComponent("PlannedOperatingTime", denominator.SourceIdentity, denominator.Dimension, denominator.Aggregate),
            offending);

        void ValidateAggregate(OperationalMetricComponent component, string key)
        {
            var inputs = selected.Where(input => input.Fact.Key == key).ToArray();
            var reaggregated = MetricInputContributionAggregator.Aggregate(revision.StreamId, inputs);
            var aggregate = request.EvaluationKey.PeriodId switch
            {
                OperationalMetricPeriodId.Shift => reaggregated.ShiftContributions.SingleOrDefault()?.Value,
                OperationalMetricPeriodId.ProductionDay => reaggregated.ProductionDayContributions.SingleOrDefault()?.Value,
                _ => null,
            };
            if (component.SourceIdentity.ProcessorId != revision.ProcessorId ||
                component.SourceIdentity.MachineId != request.EvaluationKey.MachineId ||
                component.SourceIdentity.PeriodId != request.EvaluationKey.PeriodId ||
                component.Dimension != MetricDimension.Duration || aggregate is null || component.Aggregate != aggregate)
            {
                throw new InvalidDataException("Conflict witness contribution membership does not reproduce the exact operand aggregate.");
            }
        }
    }
}
