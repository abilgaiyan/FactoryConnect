namespace FactoryConnect.Protocols.MTConnect;

public sealed record MtConnectDeviceDescriptor
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? Uuid { get; init; }
    public IReadOnlyList<MtConnectDataItemDescriptor> DataItems { get; init; } = [];
}
