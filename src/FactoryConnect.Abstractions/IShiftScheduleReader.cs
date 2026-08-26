namespace FactoryConnect.Abstractions;

public interface IShiftScheduleReader
{
    Task<IReadOnlyList<ShiftScheduleAssignment>> ReadAssignmentsAsync(
        SiteId siteId,
        DateOnly factoryDateFrom,
        DateOnly factoryDateTo,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ShiftCalendarOverride>> ReadExceptionsAsync(
        SiteId siteId,
        DateOnly factoryDateFrom,
        DateOnly factoryDateTo,
        CancellationToken cancellationToken);
}
