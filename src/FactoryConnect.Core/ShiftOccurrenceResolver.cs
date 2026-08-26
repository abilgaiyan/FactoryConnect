using FactoryConnect.Abstractions;

namespace FactoryConnect.Core;

public sealed class ShiftOccurrenceResolver
{
    private readonly IShiftScheduleReader _reader;

    public ShiftOccurrenceResolver(IShiftScheduleReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public Task<IReadOnlyList<ShiftOccurrence>> ResolveAsync(
        SiteId siteId,
        DateOnly factoryDateFrom,
        DateOnly factoryDateTo,
        CancellationToken cancellationToken) =>
        ResolveCoreAsync(
            siteId,
            null,
            factoryDateFrom,
            factoryDateTo,
            cancellationToken);

    public Task<IReadOnlyList<ShiftOccurrence>> ResolveAsync(
        SiteId siteId,
        ProductionLineId productionLineId,
        DateOnly factoryDateFrom,
        DateOnly factoryDateTo,
        CancellationToken cancellationToken)
    {
        if (productionLineId.IsEmpty)
        {
            throw new ArgumentException(
                "Production line ID is required when resolving a line schedule.",
                nameof(productionLineId));
        }

        return ResolveCoreAsync(
            siteId,
            productionLineId,
            factoryDateFrom,
            factoryDateTo,
            cancellationToken);
    }

    private async Task<IReadOnlyList<ShiftOccurrence>> ResolveCoreAsync(
        SiteId siteId,
        ProductionLineId? productionLineId,
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
        ValidateAssignments(assignments);

        var calendarOverrides = await _reader.ReadExceptionsAsync(
            siteId,
            factoryDateFrom,
            factoryDateTo,
            cancellationToken);
        ValidateOverrides(calendarOverrides);

        var occurrences = new List<ShiftOccurrence>();

        for (var factoryDate = factoryDateFrom;
             factoryDate < factoryDateTo;
             factoryDate = factoryDate.AddDays(1))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var scopedAssignments = SelectScope(
                assignments,
                productionLineId,
                factoryDate);

            foreach (var assignment in scopedAssignments)
            {
                if (!assignment.ActiveDays.Contains(factoryDate.DayOfWeek) ||
                    IsShutdown(calendarOverrides, assignment, factoryDate))
                {
                    continue;
                }

                occurrences.Add(CreateOccurrence(assignment, factoryDate));
            }
        }

        return occurrences
            .OrderBy(static occurrence => occurrence.StartsAtUtc)
            .ThenBy(static occurrence => occurrence.ProductionLineId?.Value, StringComparer.Ordinal)
            .ThenBy(static occurrence => occurrence.ShiftId.Value, StringComparer.Ordinal)
            .ThenBy(static occurrence => occurrence.SourceAssignmentId.Value, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<ShiftScheduleAssignment> SelectScope(
        IReadOnlyList<ShiftScheduleAssignment> assignments,
        ProductionLineId? productionLineId,
        DateOnly factoryDate)
    {
        if (productionLineId is null)
        {
            return assignments
                .Where(assignment =>
                    assignment.ProductionLineId is null &&
                    assignment.IsEffectiveOn(factoryDate))
                .ToArray();
        }

        var lineAssignments = assignments
            .Where(assignment =>
                assignment.ProductionLineId == productionLineId &&
                assignment.IsEffectiveOn(factoryDate))
            .ToArray();

        if (lineAssignments.Length > 0)
        {
            return lineAssignments;
        }

        return assignments
            .Where(assignment =>
                assignment.ProductionLineId is null &&
                assignment.IsEffectiveOn(factoryDate))
            .ToArray();
    }

    private static ShiftOccurrence CreateOccurrence(
        ShiftScheduleAssignment assignment,
        DateOnly factoryDate)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(assignment.TimeZoneId.Value);
        var localStart = factoryDate.ToDateTime(
            assignment.StartsAtLocal,
            DateTimeKind.Unspecified);
        var endDate = assignment.IsOvernight
            ? factoryDate.AddDays(1)
            : factoryDate;
        var localEnd = endDate.ToDateTime(
            assignment.EndsAtLocal,
            DateTimeKind.Unspecified);

        var startsAtUtc = ResolveLocalBoundary(timeZone, localStart, useEarlierUtc: true);
        var endsAtUtc = ResolveLocalBoundary(timeZone, localEnd, useEarlierUtc: false);

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

    private static DateTimeOffset ResolveLocalBoundary(
        TimeZoneInfo timeZone,
        DateTime localTime,
        bool useEarlierUtc)
    {
        while (timeZone.IsInvalidTime(localTime))
        {
            localTime = localTime.AddMinutes(1);
        }

        if (!timeZone.IsAmbiguousTime(localTime))
        {
            return new DateTimeOffset(
                TimeZoneInfo.ConvertTimeToUtc(localTime, timeZone),
                TimeSpan.Zero);
        }

        var candidates = timeZone
            .GetAmbiguousTimeOffsets(localTime)
            .Select(offset => new DateTimeOffset(localTime, offset).ToUniversalTime())
            .OrderBy(static candidate => candidate)
            .ToArray();

        return useEarlierUtc ? candidates[0] : candidates[^1];
    }

    private static bool IsShutdown(
        IReadOnlyList<ShiftCalendarOverride> calendarOverrides,
        ShiftScheduleAssignment assignment,
        DateOnly factoryDate) =>
        calendarOverrides.Any(calendarOverride =>
            calendarOverride.IsShutdown &&
            calendarOverride.FactoryDate == factoryDate &&
            (calendarOverride.ShiftId is null || calendarOverride.ShiftId == assignment.ShiftId));

    private static void ValidateAssignments(
        IReadOnlyList<ShiftScheduleAssignment> assignments)
    {
        foreach (var assignment in assignments)
        {
            ArgumentNullException.ThrowIfNull(assignment);
            assignment.Validate();
        }
    }

    private static void ValidateOverrides(
        IReadOnlyList<ShiftCalendarOverride> calendarOverrides)
    {
        foreach (var calendarOverride in calendarOverrides)
        {
            ArgumentNullException.ThrowIfNull(calendarOverride);
            calendarOverride.Validate();
        }
    }
}
