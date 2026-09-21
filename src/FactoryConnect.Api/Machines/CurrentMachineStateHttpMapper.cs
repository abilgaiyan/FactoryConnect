using FactoryConnect.Abstractions;

namespace FactoryConnect.Api.Machines;

internal static class CurrentMachineStateHttpMapper
{
    public static CurrentMachineStateResponse ToResponse(
        CurrentMachineStateReadResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result switch
        {
            CurrentMachineStateEvidence evidence => new CurrentMachineStateResponse(
                evidence.MachineId.Value,
                "evidence",
                Coverage(evidence.Coverage),
                new CurrentMachineStateEvidenceResponse(
                    MachineState(evidence.MachineState),
                    Freshness(evidence.Freshness),
                    Usability(evidence.Usability),
                    evidence.ReadAsOf)),
            CurrentMachineStateNoEvidence noEvidence => new CurrentMachineStateResponse(
                noEvidence.MachineId.Value,
                "no-evidence",
                Coverage(noEvidence.Coverage),
                null),
            _ => throw new InvalidOperationException(
                "Only successful current-machine-state results can be projected to a success response."),
        };
    }

    private static string Coverage(CurrentStateCoverage coverage) =>
        coverage switch
        {
            CurrentStateCoverage.Complete => "complete",
            CurrentStateCoverage.Behind => "behind",
            CurrentStateCoverage.Indeterminate => "indeterminate",
            _ => throw new InvalidOperationException(
                "Unsupported current-state coverage value."),
        };

    private static string MachineState(MachineState machineState) =>
        machineState switch
        {
            Abstractions.MachineState.Unknown => "unknown",
            Abstractions.MachineState.Stopped => "stopped",
            Abstractions.MachineState.Idle => "idle",
            Abstractions.MachineState.Running => "running",
            Abstractions.MachineState.Fault => "fault",
            _ => throw new InvalidOperationException(
                "Unsupported current machine-state evidence value."),
        };

    private static string Freshness(CurrentStateFreshness freshness) =>
        freshness switch
        {
            CurrentStateFreshness.Current => "current",
            CurrentStateFreshness.Stale => "stale",
            CurrentStateFreshness.Indeterminate => "indeterminate",
            _ => throw new InvalidOperationException(
                "Unsupported current-state freshness value."),
        };

    private static string Usability(CurrentStateUsability usability) =>
        usability switch
        {
            CurrentStateUsability.Current => "current",
            CurrentStateUsability.Stale => "stale",
            CurrentStateUsability.Indeterminate => "indeterminate",
            CurrentStateUsability.Behind => "behind",
            _ => throw new InvalidOperationException(
                "Unsupported current-state usability value."),
        };
}
