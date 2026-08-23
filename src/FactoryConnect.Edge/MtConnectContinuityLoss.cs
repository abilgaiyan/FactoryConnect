using FactoryConnect.Abstractions;

namespace FactoryConnect.Edge;

public sealed record MtConnectContinuityLoss
{
    public required MachineId MachineId { get; init; }

    public required MtConnectContinuityLossReason Reason { get; init; }

    public ulong? PreviousInstanceId { get; init; }

    public required ulong CurrentInstanceId { get; init; }

    public required ulong PreviousSequence { get; init; }

    public required ulong RecoverySequence { get; init; }
}
