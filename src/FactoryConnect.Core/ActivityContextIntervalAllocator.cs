using System.Security.Cryptography;
using System.Text;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Core;

public static class ActivityContextIntervalAllocator
{
    public static IReadOnlyList<ContextualizedActivityInterval> Allocate(
        DurableMachineActivityPeriod sourceActivity,
        IReadOnlyList<ShiftOccurrence> shiftOccurrences,
        IReadOnlyList<ProductionContextAssignment> contextAssignments)
    {
        ArgumentNullException.ThrowIfNull(sourceActivity);
        ArgumentNullException.ThrowIfNull(shiftOccurrences);
        ArgumentNullException.ThrowIfNull(contextAssignments);

        var source = sourceActivity.Period;
        if (source.EndedAt <= source.StartedAt)
        {
            throw new ArgumentException(
                "Source activity period must have a positive duration.",
                nameof(sourceActivity));
        }

        var shifts = ValidateAndSelectShifts(source, shiftOccurrences);
        var contexts = ValidateAndSelectContexts(source, contextAssignments);
        var boundaries = BuildBoundaries(source, shifts, contexts);
        var output = new List<ContextualizedActivityInterval>(boundaries.Count - 1);

        for (var index = 0; index < boundaries.Count - 1; index++)
        {
            var startsAt = boundaries[index];
            var endsAt = boundaries[index + 1];
            if (endsAt <= startsAt)
            {
                continue;
            }

            var shift = FindShift(shifts, startsAt, endsAt);
            var context = FindContext(contexts, startsAt, endsAt);
            ValidateScopeCompatibility(shift, context);

            output.Add(new ContextualizedActivityInterval
            {
                Id = CreateId(sourceActivity, startsAt, endsAt, shift, context),
                SourceProcessorId = sourceActivity.ProcessorId,
                SourcePosition = sourceActivity.Position,
                SourceStreamId = sourceActivity.StreamId,
                SourceInstanceId = sourceActivity.InstanceId,
                SourceSequence = sourceActivity.Sequence,
                CompanyId = context?.CompanyId ?? shift.CompanyId,
                SiteId = context?.SiteId ?? shift.SiteId,
                ProductionLineId = context?.ProductionLineId ?? shift.ProductionLineId,
                MachineId = source.MachineId,
                State = source.State,
                StartsAtUtc = startsAt,
                EndsAtUtc = endsAt,
                ShiftId = shift.ShiftId,
                ShiftScheduleAssignmentId = shift.SourceAssignmentId,
                ProductionContextAssignmentId = context?.Id,
                ProductionOrderId = context?.ProductionOrderId,
                OperationId = context?.OperationId,
                PartId = context?.PartId,
                OperatorId = context?.OperatorId,
            });
        }

        ValidateConservation(source, output);
        return output;
    }

    private static ShiftOccurrence[] ValidateAndSelectShifts(
        MachineActivityPeriod source,
        IReadOnlyList<ShiftOccurrence> shiftOccurrences)
    {
        foreach (var shift in shiftOccurrences)
        {
            ArgumentNullException.ThrowIfNull(shift);

            if (shift.SourceAssignmentId.IsEmpty)
            {
                throw new ArgumentException(
                    "Shift schedule assignment ID is required.",
                    nameof(shiftOccurrences));
            }

            if (shift.CompanyId.IsEmpty)
            {
                throw new ArgumentException(
                    "Shift company ID is required.",
                    nameof(shiftOccurrences));
            }

            if (shift.SiteId.IsEmpty)
            {
                throw new ArgumentException(
                    "Shift site ID is required.",
                    nameof(shiftOccurrences));
            }

            if (shift.ProductionLineId is { IsEmpty: true })
            {
                throw new ArgumentException(
                    "Shift production line ID cannot be empty when specified.",
                    nameof(shiftOccurrences));
            }

            if (shift.ShiftId.IsEmpty)
            {
                throw new ArgumentException(
                    "Shift ID is required.",
                    nameof(shiftOccurrences));
            }
        }

        var shifts = shiftOccurrences
            .Where(shift =>
                shift.EndsAtUtc > source.StartedAt &&
                shift.StartsAtUtc < source.EndedAt)
            .OrderBy(static shift => shift.StartsAtUtc)
            .ThenBy(static shift => shift.EndsAtUtc)
            .ThenBy(static shift => shift.SourceAssignmentId.Value, StringComparer.Ordinal)
            .ToArray();

        if (shifts.Length == 0)
        {
            throw new InvalidOperationException(
                "Shift occurrences must fully cover the source activity period.");
        }

        var cursor = source.StartedAt;
        foreach (var shift in shifts)
        {
            if (shift.EndsAtUtc <= shift.StartsAtUtc)
            {
                throw new InvalidOperationException(
                    $"Shift occurrence '{shift.SourceAssignmentId}' must have a positive duration.");
            }

            var start = shift.StartsAtUtc < source.StartedAt
                ? source.StartedAt
                : shift.StartsAtUtc;
            var end = shift.EndsAtUtc > source.EndedAt
                ? source.EndedAt
                : shift.EndsAtUtc;

            if (start < cursor)
            {
                throw new InvalidOperationException(
                    "Shift occurrences overlap within the source activity period.");
            }

            if (start > cursor)
            {
                throw new InvalidOperationException(
                    "Shift occurrences must fully cover the source activity period.");
            }

            cursor = end;
        }

        if (cursor != source.EndedAt)
        {
            throw new InvalidOperationException(
                "Shift occurrences must fully cover the source activity period.");
        }

        return shifts;
    }

    private static ProductionContextAssignment[] ValidateAndSelectContexts(
        MachineActivityPeriod source,
        IReadOnlyList<ProductionContextAssignment> contextAssignments)
    {
        foreach (var context in contextAssignments)
        {
            ArgumentNullException.ThrowIfNull(context);
            context.Validate();

            if (context.MachineId != source.MachineId)
            {
                throw new InvalidOperationException(
                    $"Production context assignment '{context.Id}' belongs to a different machine.");
            }
        }

        var contexts = contextAssignments
            .Where(context => context.Intersects(source.StartedAt, source.EndedAt))
            .OrderBy(static context => context.EffectiveFrom)
            .ThenBy(static context => context.Id.Value, StringComparer.Ordinal)
            .ToArray();

        for (var index = 1; index < contexts.Length; index++)
        {
            var previous = contexts[index - 1];
            var context = contexts[index];
            if (previous.EffectiveTo is null || previous.EffectiveTo.Value > context.EffectiveFrom)
            {
                throw new InvalidOperationException(
                    "Production context assignments overlap within the source activity period.");
            }
        }

        return contexts;
    }

    private static void ValidateScopeCompatibility(
        ShiftOccurrence shift,
        ProductionContextAssignment? context)
    {
        if (context is null)
        {
            return;
        }

        if (context.CompanyId != shift.CompanyId)
        {
            throw new InvalidOperationException(
                "Shift occurrence and production context belong to different companies.");
        }

        if (context.SiteId != shift.SiteId)
        {
            throw new InvalidOperationException(
                "Shift occurrence and production context belong to different sites.");
        }

        if (shift.ProductionLineId is { } shiftLineId &&
            context.ProductionLineId != shiftLineId)
        {
            throw new InvalidOperationException(
                "Line-specific shift occurrence and production context belong to different production lines.");
        }
    }

    private static List<DateTimeOffset> BuildBoundaries(
        MachineActivityPeriod source,
        IReadOnlyList<ShiftOccurrence> shifts,
        IReadOnlyList<ProductionContextAssignment> contexts)
    {
        var boundaries = new SortedSet<DateTimeOffset>
        {
            source.StartedAt,
            source.EndedAt,
        };

        foreach (var shift in shifts)
        {
            if (shift.StartsAtUtc > source.StartedAt && shift.StartsAtUtc < source.EndedAt)
            {
                boundaries.Add(shift.StartsAtUtc);
            }

            if (shift.EndsAtUtc > source.StartedAt && shift.EndsAtUtc < source.EndedAt)
            {
                boundaries.Add(shift.EndsAtUtc);
            }
        }

        foreach (var context in contexts)
        {
            if (context.EffectiveFrom > source.StartedAt && context.EffectiveFrom < source.EndedAt)
            {
                boundaries.Add(context.EffectiveFrom);
            }

            if (context.EffectiveTo is { } effectiveTo &&
                effectiveTo > source.StartedAt &&
                effectiveTo < source.EndedAt)
            {
                boundaries.Add(effectiveTo);
            }
        }

        return boundaries.ToList();
    }

    private static ShiftOccurrence FindShift(
        IReadOnlyList<ShiftOccurrence> shifts,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt)
    {
        var matches = shifts
            .Where(shift => shift.StartsAtUtc <= startsAt && shift.EndsAtUtc >= endsAt)
            .ToArray();

        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException("No shift occurrence covers an allocated interval."),
            _ => throw new InvalidOperationException("Multiple shift occurrences cover an allocated interval."),
        };
    }

    private static ProductionContextAssignment? FindContext(
        IReadOnlyList<ProductionContextAssignment> contexts,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt)
    {
        var matches = contexts
            .Where(context =>
                context.EffectiveFrom <= startsAt &&
                (context.EffectiveTo is null || context.EffectiveTo.Value >= endsAt))
            .ToArray();

        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException("Multiple production contexts cover an allocated interval."),
        };
    }

    private static ContextualizedActivityIntervalId CreateId(
        DurableMachineActivityPeriod sourceActivity,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        ShiftOccurrence shift,
        ProductionContextAssignment? context)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(sourceActivity.ProcessorId.Value);
            writer.Write(sourceActivity.Position.Value);
            writer.Write(sourceActivity.StreamId.MachineId.Value.ToByteArray());
            writer.Write(sourceActivity.StreamId.StreamKey);
            writer.Write(sourceActivity.InstanceId);
            writer.Write(sourceActivity.Sequence);
            writer.Write(startsAt.UtcTicks);
            writer.Write(endsAt.UtcTicks);
            writer.Write(shift.SourceAssignmentId.Value);
            writer.Write(context is not null);
            if (context is not null)
            {
                writer.Write(context.Id.Value);
            }
        }

        var bytes = SHA256.HashData(stream.ToArray());
        return new ContextualizedActivityIntervalId(Convert.ToHexString(bytes));
    }

    private static void ValidateConservation(
        MachineActivityPeriod source,
        List<ContextualizedActivityInterval> output)
    {
        if (output.Count == 0)
        {
            throw new InvalidOperationException("Activity allocation produced no intervals.");
        }

        var cursor = source.StartedAt;
        var total = TimeSpan.Zero;

        foreach (var interval in output)
        {
            if (interval.StartsAtUtc != cursor || interval.EndsAtUtc <= interval.StartsAtUtc)
            {
                throw new InvalidOperationException(
                    "Activity allocation introduced a gap, overlap, or zero-length interval.");
            }

            cursor = interval.EndsAtUtc;
            total += interval.Duration;
        }

        if (cursor != source.EndedAt || total != source.Duration)
        {
            throw new InvalidOperationException(
                "Activity allocation must preserve the source activity duration.");
        }
    }
}
