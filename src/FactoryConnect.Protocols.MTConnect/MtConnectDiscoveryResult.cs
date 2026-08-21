namespace FactoryConnect.Protocols.MTConnect;

public sealed record MtConnectDiscoveryResult
{
    public string? AgentInstanceId { get; init; }
    public string? AgentVersion { get; init; }
    public IReadOnlyList<MtConnectDeviceDescriptor> Devices { get; init; } = [];
}
