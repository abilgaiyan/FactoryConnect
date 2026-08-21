namespace FactoryConnect.Abstractions;

public sealed record PartDefinition
{
    public required PartId Id { get; init; }
    public required CompanyId CompanyId { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public string? Operation { get; init; }
}
