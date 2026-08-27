namespace FactoryConnect.Abstractions;

public sealed record PlannedProductionWindow
{
    public required TimeOnly StartsAtLocal { get; init; }
    public required TimeOnly EndsAtLocal { get; init; }

    public bool IsOvernight => EndsAtLocal < StartsAtLocal;

    public void Validate()
    {
        if (StartsAtLocal == EndsAtLocal)
        {
            throw new ArgumentException(
                "Planned production window start and end times must be different.",
                nameof(EndsAtLocal));
        }
    }

    internal static void ValidateNonOverlapping(
        IReadOnlyList<PlannedProductionWindow> windows,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(windows);

        var segments = new List<(long Start, long End)>();
        foreach (var window in windows)
        {
            ArgumentNullException.ThrowIfNull(window);
            window.Validate();

            if (window.IsOvernight)
            {
                segments.Add((window.StartsAtLocal.Ticks, TimeSpan.TicksPerDay));
                segments.Add((0, window.EndsAtLocal.Ticks));
            }
            else
            {
                segments.Add((window.StartsAtLocal.Ticks, window.EndsAtLocal.Ticks));
            }
        }

        segments.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        for (var index = 1; index < segments.Count; index++)
        {
            if (segments[index].Start < segments[index - 1].End)
            {
                throw new ArgumentException(
                    "Planned production windows must not overlap.",
                    parameterName);
            }
        }
    }
}
