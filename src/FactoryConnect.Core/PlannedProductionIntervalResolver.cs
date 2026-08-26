using FactoryConnect.Abstractions;

namespace FactoryConnect.Core;

public sealed class PlannedProductionIntervalResolver
{
    private readonly IPlannedProductionScheduleReader _reader;

    public PlannedProductionIntervalResolver(IPlannedProductionScheduleReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public Task<IReadOnlyList<PlannedProductionInterval>> ResolveAsync(
        SiteId siteId,
        DateOnly factoryDateFrom,
        DateOnly factoryDateTo,
        CancellationToken cancellationToken) =>
        ResolveCoreAsync(siteId, null, factoryDateFrom, factoryDateTo, cancellationToken);

    public Task<IReadOnlyList<PlannedProductionInterval>> ResolveAsync(
        SiteId siteId,
        ProductionLineId productionLineId,
        DateOnly factoryDateFrom,
        DateOnly factoryDateTo,
        CancellationToken cancellationToken)
    {
        if (productionLineId.IsEmpty)
        {
            throw new ArgumentException("Production line ID is required.", nameof(productionLineId));
        }

        return ResolveCoreAsync(siteId, productionLineId, factoryDateFrom, factoryDateTo, cancellationToken);
    }

    private async Task<IReadOnlyList<PlannedProductionInterval>> ResolveCoreAsync(
        SiteId siteId,
        ProductionLineId? productionLineId,
        DateOnly factoryDateFrom,
        DateOnly factoryDateTo,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(factoryDateTo, factoryDateFrom);

        var assignments = await _reader.ReadAssignmentsAsync(
            siteId, factoryDateFrom, factoryDateTo, cancellationToken);
        var overrides = await _reader.ReadOverridesAsync(
            siteId, factoryDateFrom, factoryDateTo, cancellationToken);

        foreach (var assignment in assignments)
        {
            ArgumentNullException.ThrowIfNull(assignment);
            assignment.Validate();
            if (assignment.SiteId != siteId)
            {
                throw new InvalidOperationException("Planned production provider returned an assignment for another site.");
            }
        }

        foreach (var calendarOverride in overrides)
        {
            ArgumentNullException.ThrowIfNull(calendarOverride);
            calendarOverride.Validate();
            if (calendarOverride.SiteId != siteId ||
                calendarOverride.FactoryDate < factoryDateFrom ||
                calendarOverride.FactoryDate >= factoryDateTo)
            {
                throw new InvalidOperationException("Planned production provider returned an override outside the requested scope.");
            }
        }

        var output = new List<PlannedProductionInterval>();

        for (var date = factoryDateFrom; date < factoryDateTo; date = date.AddDays(1))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var assignment = SelectAssignment(assignments, productionLineId, date);
            if (assignment is null || !assignment.ActiveDays.Contains(date.DayOfWeek))
            {
                continue;
            }

            var calendarOverride = SelectOverride(overrides, productionLineId, date);
            if (calendarOverride?.IsShutdown == true)
            {
                continue;
            }

            var plannedWindows = calendarOverride?.ReplacementPlannedWindows ?? assignment.PlannedWindows;
            var planned = plannedWindows
                .Select(window => ResolveWindow(assignment, date, window))
                .OrderBy(static interval => interval.Start)
                .ToList();
            var breaks = assignment.BreakWindows
                .Select(window => ResolveWindow(assignment, date, window))
                .OrderBy(static interval => interval.Start)
                .ToArray();

            foreach (var interval in Subtract(planned, breaks))
            {
                output.Add(new PlannedProductionInterval
                {
                    SourceAssignmentId = assignment.Id,
                    CompanyId = assignment.CompanyId,
                    SiteId = assignment.SiteId,
                    ProductionLineId = assignment.ProductionLineId,
                    FactoryDate = date,
                    StartsAtUtc = interval.Start,
                    EndsAtUtc = interval.End,
                });
            }
        }

        return output
            .OrderBy(static interval => interval.StartsAtUtc)
            .ThenBy(static interval => interval.ProductionLineId?.Value, StringComparer.Ordinal)
            .ThenBy(static interval => interval.SourceAssignmentId.Value, StringComparer.Ordinal)
            .ToArray();
    }

    private static PlannedProductionScheduleAssignment? SelectAssignment(
        IReadOnlyList<PlannedProductionScheduleAssignment> assignments,
        ProductionLineId? productionLineId,
        DateOnly date)
    {
        var lineMatches = productionLineId is null
            ? Array.Empty<PlannedProductionScheduleAssignment>()
            : assignments.Where(assignment =>
                assignment.ProductionLineId == productionLineId && assignment.IsEffectiveOn(date)).ToArray();

        if (lineMatches.Length > 1)
        {
            throw new InvalidOperationException("Multiple line-specific planned production schedules are effective for the same date.");
        }

        if (lineMatches.Length == 1)
        {
            return lineMatches[0];
        }

        var siteMatches = assignments.Where(assignment =>
            assignment.ProductionLineId is null && assignment.IsEffectiveOn(date)).ToArray();

        return siteMatches.Length switch
        {
            0 => null,
            1 => siteMatches[0],
            _ => throw new InvalidOperationException("Multiple site-wide planned production schedules are effective for the same date."),
        };
    }

    private static PlannedProductionCalendarOverride? SelectOverride(
        IReadOnlyList<PlannedProductionCalendarOverride> overrides,
        ProductionLineId? productionLineId,
        DateOnly date)
    {
        var lineMatches = productionLineId is null
            ? Array.Empty<PlannedProductionCalendarOverride>()
            : overrides.Where(item => item.FactoryDate == date && item.ProductionLineId == productionLineId).ToArray();

        if (lineMatches.Length > 1)
        {
            throw new InvalidOperationException("Multiple line-specific planned production overrides exist for the same date.");
        }

        if (lineMatches.Length == 1)
        {
            return lineMatches[0];
        }

        var siteMatches = overrides.Where(item =>
            item.FactoryDate == date && item.ProductionLineId is null).ToArray();

        return siteMatches.Length switch
        {
            0 => null,
            1 => siteMatches[0],
            _ => throw new InvalidOperationException("Multiple site-wide planned production overrides exist for the same date."),
        };
    }

    private static (DateTimeOffset Start, DateTimeOffset End) ResolveWindow(
        PlannedProductionScheduleAssignment assignment,
        DateOnly date,
        PlannedProductionWindow window)
    {
        window.Validate();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(assignment.TimeZoneId.Value);
        var localStart = date.ToDateTime(window.StartsAtLocal, DateTimeKind.Unspecified);
        var endDate = window.IsOvernight ? date.AddDays(1) : date;
        var localEnd = endDate.ToDateTime(window.EndsAtLocal, DateTimeKind.Unspecified);
        return (ResolveBoundary(zone, localStart, true), ResolveBoundary(zone, localEnd, false));
    }

    private static DateTimeOffset ResolveBoundary(TimeZoneInfo zone, DateTime localTime, bool earlier)
    {
        while (zone.IsInvalidTime(localTime))
        {
            localTime = localTime.AddMinutes(1);
        }

        if (!zone.IsAmbiguousTime(localTime))
        {
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localTime, zone), TimeSpan.Zero);
        }

        var candidates = zone.GetAmbiguousTimeOffsets(localTime)
            .Select(offset => new DateTimeOffset(localTime, offset).ToUniversalTime())
            .OrderBy(static value => value)
            .ToArray();
        return earlier ? candidates[0] : candidates[^1];
    }

    private static IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> Subtract(
        List<(DateTimeOffset Start, DateTimeOffset End)> planned,
        (DateTimeOffset Start, DateTimeOffset End)[] breaks)
    {
        foreach (var source in planned)
        {
            var fragments = new List<(DateTimeOffset Start, DateTimeOffset End)> { source };
            foreach (var pause in breaks)
            {
                var next = new List<(DateTimeOffset Start, DateTimeOffset End)>();
                foreach (var fragment in fragments)
                {
                    if (pause.End <= fragment.Start || pause.Start >= fragment.End)
                    {
                        next.Add(fragment);
                        continue;
                    }

                    if (pause.Start > fragment.Start)
                    {
                        next.Add((fragment.Start, pause.Start));
                    }

                    if (pause.End < fragment.End)
                    {
                        next.Add((pause.End, fragment.End));
                    }
                }

                fragments = next;
            }

            foreach (var fragment in fragments.Where(static item => item.End > item.Start))
            {
                yield return fragment;
            }
        }
    }
}
