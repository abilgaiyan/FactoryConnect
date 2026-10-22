using FactoryConnect.Abstractions;

namespace FactoryConnect.Core;

public static class PlannedProductionBreakPlacement
{
    public static IReadOnlyList<PlannedProductionWindow> Resolve(
        IReadOnlyList<PlannedProductionWindow> planned,
        IReadOnlyList<PlannedProductionWindow> breaks,
        bool replacement = false)
    {
        var output = new List<PlannedProductionWindow>();
        foreach (var pause in breaks)
        {
            pause.Validate();
            var candidates = (pause.HasExplicitStartDayOffset
                ? new[] { pause }
                : new[] { pause with { StartDayOffset = 0 }, pause with { StartDayOffset = 1 } })
                .Where(candidate => planned.Any(window =>
                    replacement ? Intersects(candidate, window) : Contains(window, candidate)))
                .ToArray();
            if (replacement && candidates.Length == 0)
            {
                continue;
            }
            if (candidates.Length != 1)
            {
                throw new InvalidOperationException("Break must have one unambiguous business-date placement.");
            }
            var resolved = candidates[0];
            var owners = planned.Count(window => Intersects(resolved, window));
            if (owners != 1 || output.Any(other => Intersects(other, resolved)))
            {
                throw new InvalidOperationException("Effective break ownership is overlapping or ambiguous.");
            }
            output.Add(resolved);
        }
        return output;
    }

    private static long Start(PlannedProductionWindow window) =>
        window.StartDayOffset * TimeSpan.TicksPerDay + window.StartsAtLocal.Ticks;
    private static long End(PlannedProductionWindow window) =>
        window.StartDayOffset * TimeSpan.TicksPerDay + window.EndsAtLocal.Ticks +
        (window.IsOvernight ? TimeSpan.TicksPerDay : 0);
    private static bool Contains(PlannedProductionWindow outer, PlannedProductionWindow inner) =>
        Start(outer) <= Start(inner) && End(inner) <= End(outer);
    private static bool Intersects(PlannedProductionWindow left, PlannedProductionWindow right) =>
        Start(left) < End(right) && Start(right) < End(left);
}
