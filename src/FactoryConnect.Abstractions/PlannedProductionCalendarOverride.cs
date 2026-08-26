namespace FactoryConnect.Abstractions;

public sealed record PlannedProductionCalendarOverride
{
    public required SiteId SiteId { get; init; }
    public ProductionLineId? ProductionLineId { get; init; }
    public required DateOnly FactoryDate { get; init; }
    public bool IsShutdown { get; init; }
    public IReadOnlyList<PlannedProductionWindow>? ReplacementPlannedWindows { get; init; }

    public void Validate()
    {
        if (SiteId.IsEmpty)
        {
            throw new ArgumentException("Site ID is required.", nameof(SiteId));
        }

        if (ProductionLineId is { IsEmpty: true })
        {
            throw new ArgumentException(
                "Production line ID cannot be empty when specified.",
                nameof(ProductionLineId));
        }

        if (IsShutdown && ReplacementPlannedWindows is { Count: > 0 })
        {
            throw new ArgumentException(
                "Shutdown overrides cannot define replacement planned windows.",
                nameof(ReplacementPlannedWindows));
        }

        if (ReplacementPlannedWindows is null)
        {
            return;
        }

        foreach (var window in ReplacementPlannedWindows)
        {
            ArgumentNullException.ThrowIfNull(window);
            window.Validate();
        }
    }
}
