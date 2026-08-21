namespace FactoryConnect.Abstractions;

public sealed record OperatorDefinition
{
    public required OperatorId Id { get; init; }
    public required CompanyId CompanyId { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
}
