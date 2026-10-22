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
        var requestedFrom = factoryDateFrom;
        var requestedTo = factoryDateTo;
        factoryDateFrom = DateOnly.FromDayNumber(Math.Max(DateOnly.MinValue.DayNumber, factoryDateFrom.DayNumber - 2));
        factoryDateTo = DateOnly.FromDayNumber(Math.Min(DateOnly.MaxValue.DayNumber, factoryDateTo.DayNumber + 2));


        var assignments = await _reader.ReadAssignmentsAsync(
            siteId,
            factoryDateFrom,
            factoryDateTo,
            cancellationToken);
        ShiftScheduleCollectionValidator.ValidateAssignments(
            assignments,
            siteId,
            factoryDateFrom,
            factoryDateTo);

        var calendarOverrides = await _reader.ReadExceptionsAsync(
            siteId,
            factoryDateFrom,
            factoryDateTo,
            cancellationToken);
        ShiftScheduleCollectionValidator.ValidateOverrides(
            calendarOverrides,
            siteId,
            factoryDateFrom,
            factoryDateTo);

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

        var physical = occurrences.OrderBy(item => item.StartsAtUtc).ToArray();
        for (var index = 1; index < physical.Length; index++)
        {
            if (physical[index].StartsAtUtc < physical[index - 1].EndsAtUtc)
            {
                throw new InvalidOperationException("Resolved shifts overlap across business dates.");
            }
        }

        return occurrences
            .Where(item => item.FactoryDate >= requestedFrom && item.FactoryDate < requestedTo)
            .OrderBy(static occurrence => occurrence.StartsAtUtc)
            .ThenBy(static occurrence => occurrence.ProductionLineId?.Value, StringComparer.Ordinal)
            .ThenBy(static occurrence => occurrence.ShiftId.Value, StringComparer.Ordinal)
            .ThenBy(static occurrence => occurrence.SourceAssignmentId.Value, StringComparer.Ordinal)
            .ToArray();
    }

    private static ShiftScheduleAssignment[] SelectScope(
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
        if (factoryDate.DayNumber + assignment.StartDayOffset + (assignment.IsOvernight ? 1 : 0) > DateOnly.MaxValue.DayNumber)
        {
            throw new InvalidOperationException("Shift physical interval exceeds the supported calendar.");
        }
        var startDate = factoryDate.AddDays(assignment.StartDayOffset);
        var localStart = startDate.ToDateTime(
            assignment.StartsAtLocal,
            DateTimeKind.Unspecified);
        var endDate = assignment.IsOvernight
            ? startDate.AddDays(1)
            : startDate;
        var localEnd = endDate.ToDateTime(
            assignment.EndsAtLocal,
            DateTimeKind.Unspecified);

        var startsAtUtc = ResolveLocalBoundary(timeZone, localStart, useEarlierUtc: true);
        var endsAtUtc = ResolveLocalBoundary(timeZone, localEnd, useEarlierUtc: false);

        return new ShiftOccurrence
        {
            SourceAssignmentId = assignment.Id,
            CompanyId = assignment.CompanyId,
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
            calendarOverride.SiteId == assignment.SiteId &&
            calendarOverride.IsShutdown &&
            calendarOverride.FactoryDate == factoryDate &&
            (calendarOverride.ShiftId is null || calendarOverride.ShiftId == assignment.ShiftId));
}
