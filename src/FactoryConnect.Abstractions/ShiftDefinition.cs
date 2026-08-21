namespace FactoryConnect.Abstractions;

public sealed record ShiftDefinition
{
    public required ShiftId Id { get; init; }
    public required SiteId SiteId { get; init; }
    public required string Name { get; init; }
    public required TimeOnly StartsAt { get; init; }
    public required TimeOnly EndsAt { get; init; }
}
