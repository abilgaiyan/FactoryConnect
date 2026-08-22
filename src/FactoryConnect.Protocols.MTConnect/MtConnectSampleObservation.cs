using FactoryConnect.Abstractions;

namespace FactoryConnect.Protocols.MTConnect;

public sealed record MtConnectSampleObservation
{
    public required ulong Sequence { get; init; }

    public required MachineObservation Observation { get; init; }
}
