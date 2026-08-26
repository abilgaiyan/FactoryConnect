using FactoryConnect.Abstractions;

namespace FactoryConnect.Core;

public sealed class ShiftOccurrenceResolver
{
    private readonly IShiftScheduleReader _reader;

    public ShiftOccurrenceResolver(IShiftScheduleReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public async Task<IReadOnlyList<ShiftOccurrence>> ResolveAsync(
        SiteId siteId,
        DateOnly factoryDateFrom,
        DateOnly factoryDateTo,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(factoryDateTo, factoryDateFrom);

        var assignments = await _reader.ReadAssignmentsAsync(
            siteId,
            factoryDateFrom,
            factoryDateTo,
            cancellationToken);
        var exceptions = await _reader.ReadExceptionsAsync(
            siteId,
            factoryDateFrom,
            factoryDateTo,
            cancellationToken);

        var occurrences = new List<ShiftOccurrence>();

        for (var factoryDate = factoryDateFrom;
             factoryDate < factoryDateTo;
             factoryDate = factoryDate.AddDays(1))
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var assignment in assignments)
            {
                if (!assignment.IsEffectiveOn(factoryDate) ||
                    !assignment.ActiveDays.Contains(factoryDate.DayOfWeek) ||
                    IsShutdown(exceptions, assignment, factoryDate))
                {
                    continue;
                }

                occurrences.Add(CreateOccurrence(assignment, factoryDate));
            }
        }

        return occurrences
            .OrderBy(static occurrence => occurrence.StartsAtUtc)
            .ThenBy(static occurrence => occurrence.ShiftId.Value, StringComparer.Ordinal)
            .ThenBy(static occurrence => occurrence.SourceAssignmentId.Value, StringComparer.Ordinal)
            .ToArray();
    }

    private static ShiftOccurrence CreateOccurrence(
        ShiftScheduleAssignment assignment,
        DateOnly factoryDate)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(assignment.TimeZoneId.Value);
        var localStart = factoryDate.ToDateTime(assignment.StartsAtLocal, DateTimeKind.Unspecified);
        var endDate = assignment.IsOvernight
            ? factoryDate.AddDays(1)
            : factoryDate;
        var localEnd = endDate.ToDateTime(assignment.EndsAtLocal, DateTimeKind.Unspecified);

        if (timeZone.IsInvalidTime(localStart) || timeZone.IsInvalidTime(localEnd))
        {
            throw new InvalidOperationException(
                $"Shift '{assignment.ShiftId}' resolves to an invalid local time in time zone '{assignment.TimeZoneId}'.");
        }

        if (timeZone.IsAmbiguousTime(localStart) || timeZone.IsAmbiguousTime(localEnd))
        {
            throw new InvalidOperationException(
                $"Shift '{assignment.ShiftId}' resolves to an ambiguous local time in time zone '{assignment.TimeZoneId}'.");
        }

        var startsAtUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart, timeZone), TimeSpan.Zero);
        var endsAtUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localEnd, timeZone), TimeSpan.Zero);

        return new ShiftOccurrence
        {
            SourceAssignmentId = assignment.Id,
            ShiftId = assignment.ShiftId,
            SiteId = assignment.SiteId,
            ProductionLineId = assignment.ProductionLineId,
            FactoryDate = factoryDate,
            StartsAtUtc = startsAtUtc,
            EndsAtUtc = endsAtUtc,
        };
    }

    private static bool IsShutdown(
        IReadOnlyList<ShiftCalendarException> exceptions,
        ShiftScheduleAssignment assignment,
        DateOnly factoryDate) =>
        exceptions.Any(calendarException =>
            calendarException.IsShutdown &&
            calendarException.FactoryDate == factoryDate &&
            (calendarException.ShiftId is null || calendarException.ShiftId == assignment.ShiftId));
}
