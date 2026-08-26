using FactoryConnect.Abstractions;

namespace FactoryConnect.Core;

public sealed class InMemoryShiftScheduleReader : IShiftScheduleReader
{
    private readonly List<ShiftScheduleAssignment> _assignments = [];
    private readonly List<ShiftCalendarException> _exceptions = [];

    public InMemoryShiftScheduleReader(
        IEnumerable<ShiftScheduleAssignment>? assignments = null,
        IEnumerable<ShiftCalendarException>? exceptions = null)
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
            foreach (var calendarException in exceptions)
            {
                AddException(calendarException);
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
                existing.ProductionLineId != assignment.ProductionLineId ||
                existing.ShiftId != assignment.ShiftId)
            {
                continue;
            }

            if (EffectiveRangesOverlap(existing, assignment))
            {
                throw new InvalidOperationException(
                    $"Shift schedule assignment '{assignment.Id}' overlaps assignment '{existing.Id}' for shift '{assignment.ShiftId}'.");
            }
        }

        _assignments.Add(assignment);
    }

    public void AddException(ShiftCalendarException calendarException)
    {
        ArgumentNullException.ThrowIfNull(calendarException);
        calendarException.Validate();
        _exceptions.Add(calendarException);
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
            .ThenBy(static assignment => assignment.ShiftId.Value, StringComparer.Ordinal)
            .ThenBy(static assignment => assignment.Id.Value, StringComparer.Ordinal)
            .ToArray();

        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<ShiftCalendarException>> ReadExceptionsAsync(
        SiteId siteId,
        DateOnly factoryDateFrom,
        DateOnly factoryDateTo,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(factoryDateTo, factoryDateFrom);

        IReadOnlyList<ShiftCalendarException> result = _exceptions
            .Where(calendarException =>
                calendarException.SiteId == siteId &&
                calendarException.FactoryDate >= factoryDateFrom &&
                calendarException.FactoryDate < factoryDateTo)
            .OrderBy(static calendarException => calendarException.FactoryDate)
            .ThenBy(static calendarException => calendarException.ShiftId?.Value, StringComparer.Ordinal)
            .ToArray();

        return Task.FromResult(result);
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
}
