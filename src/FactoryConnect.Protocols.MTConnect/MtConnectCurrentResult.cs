using FactoryConnect.Abstractions;

namespace FactoryConnect.Protocols.MTConnect;

public sealed record MtConnectCurrentResult
{
    public required ulong InstanceId { get; init; }

    public required ulong FirstSequence { get; init; }

    public required ulong LastSequence { get; init; }

    public required ulong NextSequence { get; init; }

    public required IReadOnlyList<MachineObservation> Observations
    {
        get;
        init;
    }
}
