using FactoryConnect.Abstractions;
using FactoryConnect.Edge;

namespace FactoryConnect.GateBControlledAgent;

/// <summary>
/// Positive generation qualification only. This is NOT a durable-write permit:
/// SQL head validation and ingestion commit are not an atomic operation.
/// </summary>
internal sealed class GateBQualifiedGenerationReporter : IMtConnectContinuityReporter
{
    private static readonly MachineId ApprovedMachine =
        new(Guid.Parse("bfa0dc97-9a68-45ed-b189-1dacb7d863f0"));

    public bool Accepted { get; private set; }

    public async ValueTask ReportAsync(
        MtConnectContinuityLoss loss,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(loss);
        if (Accepted ||
            loss.MachineId != ApprovedMachine ||
            loss.Reason != MtConnectContinuityLossReason.InstanceChanged ||
            loss.PreviousInstanceId != 1793867780UL ||
            loss.CurrentInstanceId != 9000001UL ||
            loss.PreviousSequence != 27UL ||
            loss.RecoverySequence != 1UL)
            throw new InvalidDataException("Unapproved Gate B generation or repeated admission.");

        if (await GateBAdmissionPreflight.RunAsync() != 0)
            throw new InvalidDataException("SQL publication ancestry no longer matches the approved baseline.");

        Accepted = true;
    }
}
