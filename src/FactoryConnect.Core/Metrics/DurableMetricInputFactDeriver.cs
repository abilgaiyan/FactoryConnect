using System.Security.Cryptography;
using System.Text;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Metrics;

public static class DurableMetricInputFactDeriver
{
    public static IReadOnlyList<DurableMetricInputFact> Derive(
        IReadOnlyList<ProductionTimeEligibilityInterval> eligibilityIntervals,
        IReadOnlyList<ProductionQuantityEvidence> quantityEvidence)
    {
        ArgumentNullException.ThrowIfNull(eligibilityIntervals);
        ArgumentNullException.ThrowIfNull(quantityEvidence);

        ValidateSources(eligibilityIntervals, quantityEvidence);

        var output = new List<DurableMetricInputFact>();

        foreach (var interval in eligibilityIntervals
            .OrderBy(static item => item.StartsAtUtc)
            .ThenBy(static item => item.Id.Value, StringComparer.Ordinal))
        {
            AddDurationFact(output, interval, MetricInputFactKeys.ScheduledDuration);

            if (interval.IsPlannedProductionTime)
            {
                AddDurationFact(output, interval, MetricInputFactKeys.PlannedProductionDuration);
            }

            var stateKey = interval.State switch
            {
                MachineState.Running => MetricInputFactKeys.RunningDuration,
                MachineState.Idle => MetricInputFactKeys.IdleDuration,
                MachineState.Stopped => MetricInputFactKeys.StoppedDuration,
                MachineState.Fault => MetricInputFactKeys.AlarmDuration,
                MachineState.Offline => MetricInputFactKeys.OfflineDuration,
                MachineState.Unknown => null,
                _ => null,
            };

            if (stateKey is not null)
            {
                AddDurationFact(output, interval, stateKey);
            }
        }

        foreach (var evidence in quantityEvidence
            .OrderBy(static item => item.OccurredAtUtc)
            .ThenBy(static item => item.Id.Value, StringComparer.Ordinal))
        {
            AddQuantityFact(output, evidence, MetricInputFactKeys.PartCountIncrement, evidence.PartCountIncrement);
            AddQuantityFact(output, evidence, MetricInputFactKeys.GoodQuantity, evidence.GoodQuantity);
            AddQuantityFact(output, evidence, MetricInputFactKeys.RejectedQuantity, evidence.RejectedQuantity);
        }

        return output
            .OrderBy(static fact => fact.StartsAtUtc)
            .ThenBy(static fact => fact.Key, StringComparer.Ordinal)
            .ThenBy(static fact => fact.Id.Value, StringComparer.Ordinal)
            .ToArray();
    }

    private static void ValidateSources(
        IReadOnlyList<ProductionTimeEligibilityInterval> eligibilityIntervals,
        IReadOnlyList<ProductionQuantityEvidence> quantityEvidence)
    {
        var eligibilityIds = new HashSet<ProductionTimeEligibilityIntervalId>();
        foreach (var interval in eligibilityIntervals)
        {
            ValidateEligibility(interval);
            if (!eligibilityIds.Add(interval.Id))
            {
                throw new InvalidOperationException(
                    $"Duplicate production-time eligibility interval '{interval.Id}'.");
            }
        }

        var orderedEligibility = eligibilityIntervals
            .OrderBy(static item => item.StartsAtUtc)
            .ThenBy(static item => item.EndsAtUtc)
            .ThenBy(static item => item.Id.Value, StringComparer.Ordinal)
            .ToArray();

        for (var leftIndex = 0; leftIndex < orderedEligibility.Length; leftIndex++)
        {
            var left = orderedEligibility[leftIndex];
            for (var rightIndex = leftIndex + 1; rightIndex < orderedEligibility.Length; rightIndex++)
            {
                var right = orderedEligibility[rightIndex];
                if (right.StartsAtUtc >= left.EndsAtUtc)
                {
                    break;
                }

                if (SameMetricScope(left, right) && right.StartsAtUtc < left.EndsAtUtc)
                {
                    throw new InvalidOperationException(
                        $"Production-time eligibility intervals '{left.Id}' and '{right.Id}' overlap within the same metric scope.");
                }
            }
        }

        var quantityIds = new HashSet<ProductionQuantityEvidenceId>();
        foreach (var evidence in quantityEvidence)
        {
            ArgumentNullException.ThrowIfNull(evidence);
            evidence.Validate();
            if (!quantityIds.Add(evidence.Id))
            {
                throw new InvalidOperationException(
                    $"Duplicate production quantity evidence '{evidence.Id}'.");
            }
        }
    }

    private static bool SameMetricScope(
        ProductionTimeEligibilityInterval left,
        ProductionTimeEligibilityInterval right) =>
        left.CompanyId == right.CompanyId &&
        left.SiteId == right.SiteId &&
        left.ProductionLineId == right.ProductionLineId &&
        left.MachineId == right.MachineId &&
        left.ShiftId == right.ShiftId &&
        left.ProductionContextAssignmentId == right.ProductionContextAssignmentId &&
        left.ProductionOrderId == right.ProductionOrderId &&
        left.OperationId == right.OperationId &&
        left.PartId == right.PartId &&
        left.OperatorId == right.OperatorId;

    private static void ValidateEligibility(ProductionTimeEligibilityInterval interval)
    {
        ArgumentNullException.ThrowIfNull(interval);

        if (interval.Id.IsEmpty)
        {
            throw new ArgumentException("Eligibility interval ID is required.", nameof(interval));
        }

        if (interval.CompanyId.IsEmpty || interval.SiteId.IsEmpty || interval.MachineId.IsEmpty || interval.ShiftId.IsEmpty)
        {
            throw new ArgumentException("Eligibility interval hierarchy is incomplete.", nameof(interval));
        }

        if (interval.ProductionLineId is { IsEmpty: true })
        {
            throw new ArgumentException("Production line ID cannot be empty when specified.", nameof(interval));
        }

        if (interval.EndsAtUtc <= interval.StartsAtUtc)
        {
            throw new ArgumentException("Eligibility interval must have a positive duration.", nameof(interval));
        }
    }

    private static void AddDurationFact(
        List<DurableMetricInputFact> output,
        ProductionTimeEligibilityInterval interval,
        string key)
    {
        output.Add(new DurableMetricInputFact
        {
            Id = CreateId("eligibility", interval.Id.Value, key),
            Key = key,
            Value = (decimal)interval.Duration.TotalSeconds,
            Unit = MetricInputFactUnits.Seconds,
            StartsAtUtc = interval.StartsAtUtc,
            EndsAtUtc = interval.EndsAtUtc,
            CompanyId = interval.CompanyId,
            SiteId = interval.SiteId,
            ProductionLineId = interval.ProductionLineId,
            MachineId = interval.MachineId,
            ShiftId = interval.ShiftId,
            ShiftScheduleAssignmentId = interval.ShiftScheduleAssignmentId,
            ProductionContextAssignmentId = interval.ProductionContextAssignmentId,
            ProductionOrderId = interval.ProductionOrderId,
            OperationId = interval.OperationId,
            PartId = interval.PartId,
            OperatorId = interval.OperatorId,
            IsPlannedProductionTime = interval.IsPlannedProductionTime,
            PlannedProductionScheduleAssignmentId = interval.PlannedProductionScheduleAssignmentId,
            SourceContextualizedActivityIntervalId = interval.SourceContextualizedActivityIntervalId,
            SourceEligibilityIntervalId = interval.Id,
        });
    }

    private static void AddQuantityFact(
        List<DurableMetricInputFact> output,
        ProductionQuantityEvidence evidence,
        string key,
        int? value)
    {
        if (value is null)
        {
            return;
        }

        output.Add(new DurableMetricInputFact
        {
            Id = CreateId("quantity", evidence.Id.Value, key),
            Key = key,
            Value = value.Value,
            Unit = MetricInputFactUnits.Count,
            StartsAtUtc = evidence.OccurredAtUtc,
            EndsAtUtc = evidence.OccurredAtUtc,
            CompanyId = evidence.CompanyId,
            SiteId = evidence.SiteId,
            ProductionLineId = evidence.ProductionLineId,
            MachineId = evidence.MachineId,
            ShiftId = evidence.ShiftId,
            ProductionContextAssignmentId = evidence.ProductionContextAssignmentId,
            ProductionOrderId = evidence.ProductionOrderId,
            OperationId = evidence.OperationId,
            PartId = evidence.PartId,
            OperatorId = evidence.OperatorId,
            SourceQuantityEvidenceId = evidence.Id,
        });
    }

    private static MetricInputFactId CreateId(string sourceKind, string sourceId, string key)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(sourceKind);
            writer.Write(sourceId);
            writer.Write(key);
        }

        var hash = SHA256.HashData(stream.ToArray());
        return new MetricInputFactId(Convert.ToHexString(hash));
    }
}
