using FactoryConnect.Abstractions;

namespace FactoryConnect.Core;

public sealed class InMemoryShiftScheduleReader : IShiftScheduleReader
{
    private readonly List<ShiftScheduleAssignment> _assignments = [];
    private readonly List<ShiftCalendarOverride> _exceptions = [];

    public InMemoryShiftScheduleReader(
        IEnumerable<ShiftScheduleAssignment>? assignments = null,
        IEnumerable<ShiftCalendarOverride>? exceptions = null)
    {
        if (assignments is not null)
        {
            foreach (var assignment in assignments)
            {
                AddAssignment(assignment);
            }
        }

        if (exceptions is not null)
        {
            foreach (var calendarOverride in exceptions)
            {
                AddException(calendarOverride);
            }
        }
    }

    public void AddAssignment(ShiftScheduleAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        assignment.Validate();

        if (_assignments.Any(existing => existing.Id == assignment.Id))
        {
            throw new InvalidOperationException(
                $"Shift schedule assignment '{assignment.Id}' already exists.");
        }

        foreach (var existing in _assignments)
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

        _assignments.Add(assignment);
    }

    public void AddException(ShiftCalendarOverride calendarOverride)
    {
        ArgumentNullException.ThrowIfNull(calendarOverride);
        calendarOverride.Validate();
        _exceptions.Add(calendarOverride);
    }

    public Task<IReadOnlyList<ShiftScheduleAssignment>> ReadAssignmentsAsync(
        SiteId siteId,
        DateOnly factoryDateFrom,
        DateOnly factoryDateTo,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(factoryDateTo, factoryDateFrom);

        IReadOnlyList<ShiftScheduleAssignment> result = _assignments
            .Where(assignment =>
                assignment.SiteId == siteId &&
                assignment.EffectiveFrom < factoryDateTo &&
                (assignment.EffectiveTo is null || assignment.EffectiveTo.Value > factoryDateFrom))
            .OrderBy(static assignment => assignment.EffectiveFrom)
            .ThenBy(static assignment => assignment.ProductionLineId?.Value, StringComparer.Ordinal)
            .ThenBy(static assignment => assignment.ShiftId.Value, StringComparer.Ordinal)
            .ThenBy(static assignment => assignment.Id.Value, StringComparer.Ordinal)
            .ToArray();

        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<ShiftCalendarOverride>> ReadExceptionsAsync(
        SiteId siteId,
        DateOnly factoryDateFrom,
        DateOnly factoryDateTo,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(factoryDateTo, factoryDateFrom);

        IReadOnlyList<ShiftCalendarOverride> result = _exceptions
            .Where(calendarOverride =>
                calendarOverride.SiteId == siteId &&
                calendarOverride.FactoryDate >= factoryDateFrom &&
                calendarOverride.FactoryDate < factoryDateTo)
            .OrderBy(static calendarOverride => calendarOverride.FactoryDate)
            .ThenBy(static calendarOverride => calendarOverride.ShiftId?.Value, StringComparer.Ordinal)
            .ToArray();

        return Task.FromResult(result);
    }

    private static bool SchedulesCanOverlap(
        ShiftScheduleAssignment left,
        ShiftScheduleAssignment right)
    {
        if (!EffectiveRangesOverlap(left, right))
        {
            return false;
        }

        var rangeStart = left.EffectiveFrom > right.EffectiveFrom
            ? left.EffectiveFrom
            : right.EffectiveFrom;
        var rangeEnd = EarliestEffectiveEnd(left.EffectiveTo, right.EffectiveTo);
        var inspectionEnd = rangeStart.AddDays(14);
        if (rangeEnd is not null && rangeEnd.Value < inspectionEnd)
        {
            inspectionEnd = rangeEnd.Value;
        }

        for (var leftDate = rangeStart; leftDate < inspectionEnd; leftDate = leftDate.AddDays(1))
        {
            if (!left.ActiveDays.Contains(leftDate.DayOfWeek))
            {
                continue;
            }

            var leftInterval = GetLocalInterval(left, leftDate);

            for (var rightDate = rangeStart.AddDays(-1);
                 rightDate <= inspectionEnd;
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
        var start = factoryDate.ToDateTime(
            assignment.StartsAtLocal,
            DateTimeKind.Unspecified);
        var endDate = assignment.IsOvernight
            ? factoryDate.AddDays(1)
            : factoryDate;
        var end = endDate.ToDateTime(
            assignment.EndsAtLocal,
            DateTimeKind.Unspecified);
        return (start, end);
    }

    private static bool EffectiveRangesOverlap(
        ShiftScheduleAssignment left,
        ShiftScheduleAssignment right)
    {
        var leftEndsAfterRightStarts =
            left.EffectiveTo is null || left.EffectiveTo.Value > right.EffectiveFrom;
        var rightEndsAfterLeftStarts =
            right.EffectiveTo is null || right.EffectiveTo.Value > left.EffectiveFrom;

        return leftEndsAfterRightStarts && rightEndsAfterLeftStarts;
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
