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
        var rangeStart = left.EffectiveFrom > right.EffectiveFrom
            ? left.EffectiveFrom
            : right.EffectiveFrom;
        var rangeEnd = EarliestEffectiveEnd(left.EffectiveTo, right.EffectiveTo);
        rangeStart = rangeStart.AddDays(-2);
        var inspectionEnd = rangeStart.AddDays(18);
        if (rangeEnd is not null && rangeEnd.Value.AddDays(2) < inspectionEnd)
        {
            inspectionEnd = rangeEnd.Value.AddDays(2);
        }

        for (var leftDate = rangeStart;
             leftDate < inspectionEnd;
             leftDate = leftDate.AddDays(1))
        {
            if (!left.IsEffectiveOn(leftDate) ||
                !left.ActiveDays.Contains(leftDate.DayOfWeek))
            {
                continue;
            }

            var leftInterval = GetLocalInterval(left, leftDate);

            for (var rightDate = rangeStart.AddDays(-2);
                 rightDate <= inspectionEnd.AddDays(2);
                 rightDate = rightDate.AddDays(1))
            {
                if (!right.IsEffectiveOn(rightDate) ||
                    !right.ActiveDays.Contains(rightDate.DayOfWeek))
                {
                    continue;
                }

                var rightInterval = GetLocalInterval(right, rightDate);
                if (leftInterval.Start < rightInterval.End &&
                    rightInterval.Start < leftInterval.End)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static (DateTime Start, DateTime End) GetLocalInterval(
        ShiftScheduleAssignment assignment,
        DateOnly factoryDate)
    {
        var startDate = factoryDate.AddDays(assignment.StartDayOffset);
        var start = startDate.ToDateTime(
            assignment.StartsAtLocal,
            DateTimeKind.Unspecified);
        var endDate = assignment.IsOvernight
            ? startDate.AddDays(1)
            : startDate;
        var end = endDate.ToDateTime(
            assignment.EndsAtLocal,
            DateTimeKind.Unspecified);
        return (start, end);
    }

    private static DateOnly? EarliestEffectiveEnd(DateOnly? left, DateOnly? right)
    {
        if (left is null)
        {
            return right;
        }

        if (right is null)
        {
            return left;
        }

        return left.Value < right.Value ? left : right;
    }
}
