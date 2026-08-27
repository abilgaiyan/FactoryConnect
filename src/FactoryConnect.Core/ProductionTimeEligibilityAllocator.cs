using System.Security.Cryptography;
using System.Text;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Core;

public static class ProductionTimeEligibilityAllocator
{
    public static IReadOnlyList<ProductionTimeEligibilityInterval> Allocate(
        ContextualizedActivityInterval source,
        IReadOnlyList<PlannedProductionInterval> plannedIntervals)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(plannedIntervals);

        if (source.EndsAtUtc <= source.StartsAtUtc)
        {
            throw new ArgumentException("Source contextualized activity must have positive duration.", nameof(source));
        }

        var planned = plannedIntervals
            .Where(interval => interval.EndsAtUtc > source.StartsAtUtc && interval.StartsAtUtc < source.EndsAtUtc)
            .OrderBy(static interval => interval.StartsAtUtc)
            .ThenBy(static interval => interval.EndsAtUtc)
            .ToArray();

        ValidatePlannedIntervals(source, planned);

        var boundaries = new SortedSet<DateTimeOffset> { source.StartsAtUtc, source.EndsAtUtc };
        foreach (var interval in planned)
        {
            if (interval.StartsAtUtc > source.StartsAtUtc && interval.StartsAtUtc < source.EndsAtUtc)
            {
                boundaries.Add(interval.StartsAtUtc);
            }

            if (interval.EndsAtUtc > source.StartsAtUtc && interval.EndsAtUtc < source.EndsAtUtc)
            {
                boundaries.Add(interval.EndsAtUtc);
            }
        }

        var points = boundaries.ToArray();
        var output = new List<ProductionTimeEligibilityInterval>(points.Length - 1);

        for (var index = 0; index < points.Length - 1; index++)
        {
            var startsAt = points[index];
            var endsAt = points[index + 1];
            var matching = planned
                .Where(interval => interval.StartsAtUtc <= startsAt && interval.EndsAtUtc >= endsAt)
                .ToArray();

            if (matching.Length > 1)
            {
                throw new InvalidOperationException("Multiple planned production intervals cover the same activity fragment.");
            }

            var plannedInterval = matching.Length == 1 ? matching[0] : null;
            output.Add(new ProductionTimeEligibilityInterval
            {
                Id = CreateId(source, startsAt, endsAt, plannedInterval),
                SourceContextualizedActivityIntervalId = source.Id,
                CompanyId = source.CompanyId,
                SiteId = source.SiteId,
                ProductionLineId = source.ProductionLineId,
                MachineId = source.MachineId,
                State = source.State,
                ShiftId = source.ShiftId,
                ShiftScheduleAssignmentId = source.ShiftScheduleAssignmentId,
                ProductionContextAssignmentId = source.ProductionContextAssignmentId,
                ProductionOrderId = source.ProductionOrderId,
                OperationId = source.OperationId,
                PartId = source.PartId,
                OperatorId = source.OperatorId,
                StartsAtUtc = startsAt,
                EndsAtUtc = endsAt,
                IsPlannedProductionTime = plannedInterval is not null,
                PlannedProductionScheduleAssignmentId = plannedInterval?.SourceAssignmentId,
            });
        }

        ValidateConservation(source, output);
        return output;
    }

    private static void ValidatePlannedIntervals(
        ContextualizedActivityInterval source,
        PlannedProductionInterval[] planned)
    {
        PlannedProductionInterval? previous = null;
        foreach (var interval in planned)
        {
            if (interval.CompanyId != source.CompanyId || interval.SiteId != source.SiteId)
            {
                throw new InvalidOperationException("Planned production interval hierarchy does not match contextualized activity.");
            }

            if (interval.ProductionLineId is not null && interval.ProductionLineId != source.ProductionLineId)
            {
                throw new InvalidOperationException("Planned production interval line does not match contextualized activity.");
            }

            if (interval.EndsAtUtc <= interval.StartsAtUtc)
            {
                throw new InvalidOperationException("Planned production interval must have positive duration.");
            }

            if (previous is not null && previous.EndsAtUtc > interval.StartsAtUtc)
            {
                throw new InvalidOperationException("Planned production intervals overlap.");
            }

            previous = interval;
        }
    }

    private static ProductionTimeEligibilityIntervalId CreateId(
        ContextualizedActivityInterval source,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        PlannedProductionInterval? plannedInterval)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(source.Id.Value ?? string.Empty);
            writer.Write(startsAt.UtcTicks);
            writer.Write(endsAt.UtcTicks);
            writer.Write(plannedInterval?.SourceAssignmentId.Value ?? string.Empty);
            writer.Write(plannedInterval is not null);
        }

        var hash = SHA256.HashData(stream.ToArray());
        return new ProductionTimeEligibilityIntervalId(Convert.ToHexString(hash));
    }

    private static void ValidateConservation(
        ContextualizedActivityInterval source,
        List<ProductionTimeEligibilityInterval> output)
    {
        var cursor = source.StartsAtUtc;
        var total = TimeSpan.Zero;
        foreach (var interval in output)
        {
            if (interval.StartsAtUtc != cursor || interval.EndsAtUtc <= interval.StartsAtUtc)
            {
                throw new InvalidOperationException("Eligibility allocation introduced a gap, overlap, or zero-length interval.");
            }

            cursor = interval.EndsAtUtc;
            total += interval.Duration;
        }

        if (cursor != source.EndsAtUtc || total != source.Duration)
        {
            throw new InvalidOperationException("Eligibility allocation must preserve source duration.");
        }
    }
}
