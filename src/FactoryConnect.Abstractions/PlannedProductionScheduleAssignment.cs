namespace FactoryConnect.Abstractions;

public sealed record PlannedProductionScheduleAssignment
{
    public required PlannedProductionScheduleAssignmentId Id { get; init; }
    public required CompanyId CompanyId { get; init; }
    public required SiteId SiteId { get; init; }
    public ProductionLineId? ProductionLineId { get; init; }
    public required FactoryTimeZoneId TimeZoneId { get; init; }
    public IReadOnlySet<DayOfWeek> ActiveDays { get; init; } =
        new HashSet<DayOfWeek>(Enum.GetValues<DayOfWeek>());
    public required DateOnly EffectiveFrom { get; init; }
    public DateOnly? EffectiveTo { get; init; }
    public IReadOnlyList<PlannedProductionWindow> PlannedWindows { get; init; } = [];
    public IReadOnlyList<PlannedProductionWindow> BreakWindows { get; init; } = [];

    public bool IsEffectiveOn(DateOnly factoryDate) =>
        factoryDate >= EffectiveFrom &&
        (EffectiveTo is null || factoryDate < EffectiveTo.Value);

    public void Validate()
    {
        if (Id.IsEmpty)
        {
            throw new ArgumentException("Planned production schedule assignment ID is required.", nameof(Id));
        }

        if (CompanyId.IsEmpty)
        {
            throw new ArgumentException("Company ID is required.", nameof(CompanyId));
        }

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

        if (TimeZoneId.IsEmpty)
        {
            throw new ArgumentException("Factory time zone ID is required.", nameof(TimeZoneId));
        }

        if (ActiveDays.Count == 0)
        {
            throw new ArgumentException("At least one active day is required.", nameof(ActiveDays));
        }

        if (EffectiveTo is not null && EffectiveTo.Value <= EffectiveFrom)
        {
            throw new ArgumentException(
                "EffectiveTo must be greater than EffectiveFrom when specified.",
                nameof(EffectiveTo));
        }

        foreach (var window in PlannedWindows)
        {
            ArgumentNullException.ThrowIfNull(window);
            window.Validate();
        }

        foreach (var window in BreakWindows)
        {
            ArgumentNullException.ThrowIfNull(window);
            window.Validate();
        }
    }
}
