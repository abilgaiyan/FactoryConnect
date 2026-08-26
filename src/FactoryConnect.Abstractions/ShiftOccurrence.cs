namespace FactoryConnect.Abstractions;

public sealed record ShiftOccurrence
{
    public required ShiftScheduleAssignmentId SourceAssignmentId { get; init; }
    public required ShiftId ShiftId { get; init; }
    public required SiteId SiteId { get; init; }
    public ProductionLineId? ProductionLineId { get; init; }
    public required DateOnly FactoryDate { get; init; }
    public required DateTimeOffset StartsAtUtc { get; init; }
    public required DateTimeOffset EndsAtUtc { get; init; }

    public TimeSpan Duration => EndsAtUtc - StartsAtUtc;
}
