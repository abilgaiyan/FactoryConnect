namespace FactoryConnect.Abstractions;

public interface IPlannedProductionScheduleReader
{
    Task<IReadOnlyList<PlannedProductionScheduleAssignment>> ReadAssignmentsAsync(
        SiteId siteId,
        DateOnly factoryDateFrom,
        DateOnly factoryDateTo,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PlannedProductionCalendarOverride>> ReadOverridesAsync(
        SiteId siteId,
        DateOnly factoryDateFrom,
        DateOnly factoryDateTo,
        CancellationToken cancellationToken);
}
