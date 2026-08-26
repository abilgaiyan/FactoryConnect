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
        ShiftScheduleCollectionValidator.ValidateCandidate(_assignments, assignment);
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
}
