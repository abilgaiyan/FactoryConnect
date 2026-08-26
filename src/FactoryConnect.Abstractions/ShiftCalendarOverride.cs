namespace FactoryConnect.Abstractions;

public sealed record ShiftCalendarOverride
{
    public required SiteId SiteId { get; init; }
    public required DateOnly FactoryDate { get; init; }
    public ShiftId? ShiftId { get; init; }
    public required bool IsShutdown { get; init; }

    public void Validate()
    {
        if (SiteId.IsEmpty)
        {
            throw new ArgumentException("Site ID is required.", nameof(SiteId));
        }

        if (ShiftId is { IsEmpty: true })
        {
            throw new ArgumentException(
                "Shift ID cannot be empty when specified.",
                nameof(ShiftId));
        }
    }
}
