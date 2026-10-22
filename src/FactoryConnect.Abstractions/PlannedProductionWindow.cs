namespace FactoryConnect.Abstractions;

public sealed record PlannedProductionWindow
{
    public required TimeOnly StartsAtLocal { get; init; }
    public required TimeOnly EndsAtLocal { get; init; }

    private readonly int? _startDayOffset;
    public int StartDayOffset { get => _startDayOffset ?? 0; init => _startDayOffset = value; }
    public bool HasExplicitStartDayOffset => _startDayOffset.HasValue;

    public bool IsOvernight => EndsAtLocal < StartsAtLocal;

    public void Validate()
    {
        if (StartDayOffset is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(StartDayOffset));
        }

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

            var start = window.StartDayOffset * TimeSpan.TicksPerDay + window.StartsAtLocal.Ticks;
            var end = window.StartDayOffset * TimeSpan.TicksPerDay + window.EndsAtLocal.Ticks +
                (window.IsOvernight ? TimeSpan.TicksPerDay : 0);
            segments.Add((start, end));
            if (windows.All(static item => item.StartDayOffset == 0) && window.IsOvernight && window.EndsAtLocal.Ticks > 0)
            {
                segments.Add((0, window.EndsAtLocal.Ticks));
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
