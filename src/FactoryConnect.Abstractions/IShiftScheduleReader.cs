namespace FactoryConnect.Abstractions;

public interface IShiftScheduleReader
{
    Task<IReadOnlyList<ShiftScheduleAssignment>> ReadAssignmentsAsync(
        SiteId siteId,
        DateOnly factoryDateFrom,
        DateOnly factoryDateTo,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ShiftCalendarException>> ReadExceptionsAsync(
        SiteId siteId,
        DateOnly factoryDateFrom,
        DateOnly factoryDateTo,
        CancellationToken cancellationToken);
}
