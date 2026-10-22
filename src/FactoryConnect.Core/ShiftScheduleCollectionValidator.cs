using FactoryConnect.Abstractions;

namespace FactoryConnect.Core;

internal static class ShiftScheduleCollectionValidator
{
    public static void ValidateAssignments(
        IReadOnlyList<ShiftScheduleAssignment> assignments,
        SiteId siteId,
        DateOnly factoryDateFrom,
        DateOnly factoryDateTo)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(factoryDateTo, factoryDateFrom);

        var ids = new HashSet<ShiftScheduleAssignmentId>();

        foreach (var assignment in assignments)
        {
            ArgumentNullException.ThrowIfNull(assignment);
            assignment.Validate();

            if (assignment.SiteId != siteId)
            {
                throw new InvalidOperationException(
                    $"Shift schedule assignment '{assignment.Id}' belongs to site '{assignment.SiteId}', not requested site '{siteId}'.");
            }

            if (!ids.Add(assignment.Id))
            {
                throw new InvalidOperationException(
                    $"Shift schedule assignment '{assignment.Id}' was returned more than once.");
            }

            if (!IntersectsRequestedRange(assignment, factoryDateFrom, factoryDateTo))
            {
                throw new InvalidOperationException(
                    $"Shift schedule assignment '{assignment.Id}' does not intersect the requested factory-date interval.");
            }
        }

        for (var leftIndex = 0; leftIndex < assignments.Count; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < assignments.Count; rightIndex++)
            {
                var left = assignments[leftIndex];
                var right = assignments[rightIndex];

                if (left.ProductionLineId != right.ProductionLineId)
                {
                    continue;
                }

                if (SchedulesCanOverlap(left, right))
                {
                    throw new InvalidOperationException(
                        $"Shift schedule assignment '{right.Id}' overlaps assignment '{left.Id}' in the same schedule scope.");
                }
            }
        }
    }

    public static void ValidateOverrides(
        IReadOnlyList<ShiftCalendarOverride> calendarOverrides,
        SiteId siteId,
        DateOnly factoryDateFrom,
        DateOnly factoryDateTo)
    {
        ArgumentNullException.ThrowIfNull(calendarOverrides);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(factoryDateTo, factoryDateFrom);

        foreach (var calendarOverride in calendarOverrides)
        {
            ArgumentNullException.ThrowIfNull(calendarOverride);
            calendarOverride.Validate();

            if (calendarOverride.SiteId != siteId)
            {
                throw new InvalidOperationException(
                    $"Shift calendar override belongs to site '{calendarOverride.SiteId}', not requested site '{siteId}'.");
            }

            if (calendarOverride.FactoryDate < factoryDateFrom ||
                calendarOverride.FactoryDate >= factoryDateTo)
            {
                throw new InvalidOperationException(
                    $"Shift calendar override for '{calendarOverride.FactoryDate}' falls outside the requested factory-date interval.");
            }
        }
    }

    public static void ValidateCandidate(
        IReadOnlyList<ShiftScheduleAssignment> existingAssignments,
        ShiftScheduleAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(existingAssignments);
        ArgumentNullException.ThrowIfNull(assignment);
        assignment.Validate();

        if (existingAssignments.Any(existing => existing.Id == assignment.Id))
        {
            throw new InvalidOperationException(
                $"Shift schedule assignment '{assignment.Id}' already exists.");
        }

        foreach (var existing in existingAssignments)
        {
            if (existing.SiteId != assignment.SiteId ||
                existing.ProductionLineId != assignment.ProductionLineId)
            {
                continue;
            }

            if (SchedulesCanOverlap(existing, assignment))
            {
                throw new InvalidOperationException(
                    $"Shift schedule assignment '{assignment.Id}' overlaps assignment '{existing.Id}' in the same schedule scope.");
            }
        }
    }

    private static bool IntersectsRequestedRange(
        ShiftScheduleAssignment assignment,
        DateOnly factoryDateFrom,
        DateOnly factoryDateTo) =>
        assignment.EffectiveFrom < factoryDateTo &&
        (assignment.EffectiveTo is null || assignment.EffectiveTo.Value > factoryDateFrom);

    private static bool SchedulesCanOverlap(
        ShiftScheduleAssignment left,
        ShiftScheduleAssignment right)
    {
        // Each interval starts at business day + 0/1 and lasts less than a day.
        // Compare relative business dates, intersecting each effective range separately.
        // Seven consecutive dates cover the recurring weekday pattern, including transitions.
        var lastExclusive = DateOnly.MaxValue.DayNumber + 1;
        for (var delta = -2; delta <= 2; delta++)
        {
            var from = Math.Max(left.EffectiveFrom.DayNumber, right.EffectiveFrom.DayNumber - delta);
            var to = Math.Min(left.EffectiveTo?.DayNumber ?? lastExclusive,
                (right.EffectiveTo?.DayNumber ?? lastExclusive) - delta);
            for (var day = from; day < to && day < from + 7; day++)
            {
                var leftDate = DateOnly.FromDayNumber(day);
                var rightDate = DateOnly.FromDayNumber(day + delta);
                if (!left.ActiveDays.Contains(leftDate.DayOfWeek) ||
                    !right.ActiveDays.Contains(rightDate.DayOfWeek))
                {
                    continue;
                }
                var leftInterval = GetLocalInterval(left, day);
                var rightInterval = GetLocalInterval(right, day + delta);
                if (leftInterval.Start < rightInterval.End && rightInterval.Start < leftInterval.End)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static (long Start, long End) GetLocalInterval(
        ShiftScheduleAssignment assignment, int businessDayNumber)
    {
        var midnight = ((long)businessDayNumber + assignment.StartDayOffset) * TimeSpan.TicksPerDay;
        return (midnight + assignment.StartsAtLocal.Ticks,
            midnight + assignment.EndsAtLocal.Ticks + (assignment.IsOvernight ? TimeSpan.TicksPerDay : 0));
    }
}
