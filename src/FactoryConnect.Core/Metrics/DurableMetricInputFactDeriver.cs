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
        var machineTimelines = new Dictionary<MachineId, List<ProductionTimeEligibilityInterval>>();

        foreach (var interval in eligibilityIntervals)
        {
            ArgumentNullException.ThrowIfNull(interval);
            ValidateEligibility(interval);

            if (!eligibilityIds.Add(interval.Id))
            {
                throw new InvalidOperationException($"Eligibility interval '{interval.Id}' is duplicated.");
            }

            if (!machineTimelines.TryGetValue(interval.MachineId, out var timeline))
            {
                timeline = [];
                machineTimelines.Add(interval.MachineId, timeline);
            }

            timeline.Add(interval);
        }

        foreach (var timeline in machineTimelines.Values)
        {
            timeline.Sort(static (left, right) => left.StartsAtUtc.CompareTo(right.StartsAtUtc));

            for (var index = 1; index < timeline.Count; index++)
            {
                if (timeline[index - 1].EndsAtUtc > timeline[index].StartsAtUtc)
                {
                    throw new InvalidOperationException(
                        $"Eligibility intervals for machine '{timeline[index].MachineId}' overlap.");
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
                throw new InvalidOperationException($"Production quantity evidence '{evidence.Id}' is duplicated.");
            }
        }
    }

    private static void ValidateEligibility(ProductionTimeEligibilityInterval interval)
    {
        if (interval.Id.IsEmpty)
        {
            throw new ArgumentException("Eligibility interval ID is required.", nameof(interval));
        }

        if (interval.SourceContextualizedActivityIntervalId.IsEmpty)
        {
            throw new ArgumentException("Source contextualized activity interval ID is required.", nameof(interval));
        }

        if (interval.ShiftScheduleAssignmentId.IsEmpty)
        {
            throw new ArgumentException("Shift schedule assignment ID is required.", nameof(interval));
        }

        if (interval.CompanyId.IsEmpty || interval.SiteId.IsEmpty || interval.MachineId.IsEmpty || interval.ShiftId.IsEmpty)
        {
            throw new ArgumentException("Eligibility interval hierarchy is incomplete.", nameof(interval));
        }

        if (interval.ProductionLineId is { IsEmpty: true } ||
            interval.ProductionContextAssignmentId is { IsEmpty: true } ||
            interval.ProductionOrderId is { IsEmpty: true } ||
            interval.OperationId is { IsEmpty: true } ||
            interval.PartId is { IsEmpty: true } ||
            interval.OperatorId is { IsEmpty: true })
        {
            throw new ArgumentException("Eligibility interval contains an empty optional identifier.", nameof(interval));
        }

        if (interval.IsPlannedProductionTime)
        {
            if (interval.PlannedProductionScheduleAssignmentId is null ||
                interval.PlannedProductionScheduleAssignmentId.Value.IsEmpty)
            {
                throw new ArgumentException(
                    "Planned eligibility requires a planned production schedule assignment ID.",
                    nameof(interval));
            }
        }
        else if (interval.PlannedProductionScheduleAssignmentId is not null)
        {
            throw new ArgumentException(
                "Non-planned eligibility must not reference a planned production schedule assignment.",
                nameof(interval));
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
            ProductionContextAssignmentId = interval.ProductionContextAssignmentId,
            ProductionOrderId = interval.ProductionOrderId,
            OperationId = interval.OperationId,
            PartId = interval.PartId,
            OperatorId = interval.OperatorId,
            IsPlannedProductionTime = interval.IsPlannedProductionTime,
            PlannedProductionScheduleAssignmentId = interval.PlannedProductionScheduleAssignmentId,
            ShiftScheduleAssignmentId = interval.ShiftScheduleAssignmentId,
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
