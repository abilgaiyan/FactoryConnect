namespace FactoryConnect.Protocols.MTConnect;

public sealed record MtConnectDataItemDescriptor
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public required string Type { get; init; }
    public string? Category { get; init; }
    public string? SubType { get; init; }
    public string? Units { get; init; }
    public string? ComponentId { get; init; }
    public string? ComponentName { get; init; }
    public string? ComponentType { get; init; }
}
