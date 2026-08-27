using FactoryConnect.Abstractions;

namespace FactoryConnect.Core;

public sealed class InMemoryPlannedProductionScheduleReader : IPlannedProductionScheduleReader
{
    private readonly List<PlannedProductionScheduleAssignment> _assignments = [];
    private readonly List<PlannedProductionCalendarOverride> _overrides = [];

    public InMemoryPlannedProductionScheduleReader(
        IEnumerable<PlannedProductionScheduleAssignment>? assignments = null,
        IEnumerable<PlannedProductionCalendarOverride>? overrides = null)
    {
        if (assignments is not null)
        {
            foreach (var assignment in assignments)
            {
                AddAssignment(assignment);
            }
        }

        if (overrides is not null)
        {
            foreach (var calendarOverride in overrides)
            {
                AddOverride(calendarOverride);
            }
        }
    }

    public void AddAssignment(PlannedProductionScheduleAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        assignment.Validate();

        if (_assignments.Any(existing => existing.Id == assignment.Id))
        {
            throw new InvalidOperationException(
                $"Planned production schedule assignment '{assignment.Id}' already exists.");
        }

        _assignments.Add(assignment);
    }

    public void AddOverride(PlannedProductionCalendarOverride calendarOverride)
    {
        ArgumentNullException.ThrowIfNull(calendarOverride);
        calendarOverride.Validate();
        _overrides.Add(calendarOverride);
    }

    public Task<IReadOnlyList<PlannedProductionScheduleAssignment>> ReadAssignmentsAsync(
        SiteId siteId,
        DateOnly factoryDateFrom,
        DateOnly factoryDateTo,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(factoryDateTo, factoryDateFrom);

        IReadOnlyList<PlannedProductionScheduleAssignment> result = _assignments
            .Where(assignment =>
                assignment.SiteId == siteId &&
                assignment.EffectiveFrom < factoryDateTo &&
                (assignment.EffectiveTo is null || assignment.EffectiveTo.Value > factoryDateFrom))
            .OrderBy(static assignment => assignment.EffectiveFrom)
            .ThenBy(static assignment => assignment.ProductionLineId?.Value, StringComparer.Ordinal)
            .ThenBy(static assignment => assignment.Id.Value, StringComparer.Ordinal)
            .ToArray();

        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<PlannedProductionCalendarOverride>> ReadOverridesAsync(
        SiteId siteId,
        DateOnly factoryDateFrom,
        DateOnly factoryDateTo,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(factoryDateTo, factoryDateFrom);

        IReadOnlyList<PlannedProductionCalendarOverride> result = _overrides
            .Where(calendarOverride =>
                calendarOverride.SiteId == siteId &&
                calendarOverride.FactoryDate >= factoryDateFrom &&
                calendarOverride.FactoryDate < factoryDateTo)
            .OrderBy(static calendarOverride => calendarOverride.FactoryDate)
            .ThenBy(static calendarOverride => calendarOverride.ProductionLineId?.Value, StringComparer.Ordinal)
            .ToArray();

        return Task.FromResult(result);
    }
}
