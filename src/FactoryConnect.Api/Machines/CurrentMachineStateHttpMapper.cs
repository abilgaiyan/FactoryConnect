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
                CurrentMachineStateHttpVocabulary.OutcomeEvidence,
                Coverage(evidence.Coverage),
                new CurrentMachineStateEvidenceResponse(
                    MachineState(evidence.MachineState),
                    Freshness(evidence.Freshness),
                    Usability(evidence.Usability),
                    evidence.ReadAsOf)),
            CurrentMachineStateNoEvidence noEvidence => new CurrentMachineStateResponse(
                noEvidence.MachineId.Value,
                CurrentMachineStateHttpVocabulary.OutcomeNoEvidence,
                Coverage(noEvidence.Coverage),
                null),
            _ => throw new InvalidOperationException(
                "Only successful current-machine-state results can be projected to a success response."),
        };
    }

    private static string Coverage(CurrentStateCoverage coverage) =>
        coverage switch
        {
            CurrentStateCoverage.Complete => CurrentMachineStateHttpVocabulary.CoverageComplete,
            CurrentStateCoverage.Behind => CurrentMachineStateHttpVocabulary.CoverageBehind,
            CurrentStateCoverage.Indeterminate => CurrentMachineStateHttpVocabulary.Indeterminate,
            _ => throw new InvalidOperationException(
                "Unsupported current-state coverage value."),
        };

    private static string MachineState(MachineState machineState) =>
        machineState switch
        {
            FactoryConnect.Abstractions.MachineState.Unknown => CurrentMachineStateHttpVocabulary.MachineStateUnknown,
            FactoryConnect.Abstractions.MachineState.Stopped => CurrentMachineStateHttpVocabulary.MachineStateStopped,
            FactoryConnect.Abstractions.MachineState.Idle => CurrentMachineStateHttpVocabulary.MachineStateIdle,
            FactoryConnect.Abstractions.MachineState.Running => CurrentMachineStateHttpVocabulary.MachineStateRunning,
            FactoryConnect.Abstractions.MachineState.Fault => CurrentMachineStateHttpVocabulary.MachineStateFault,
            _ => throw new InvalidOperationException(
                "Unsupported current machine-state evidence value."),
        };

    private static string Freshness(CurrentStateFreshness freshness) =>
        freshness switch
        {
            CurrentStateFreshness.Current => CurrentMachineStateHttpVocabulary.Current,
            CurrentStateFreshness.Stale => CurrentMachineStateHttpVocabulary.Stale,
            CurrentStateFreshness.Indeterminate => CurrentMachineStateHttpVocabulary.Indeterminate,
            _ => throw new InvalidOperationException(
                "Unsupported current-state freshness value."),
        };

    private static string Usability(CurrentStateUsability usability) =>
        usability switch
        {
            CurrentStateUsability.Current => CurrentMachineStateHttpVocabulary.Current,
            CurrentStateUsability.Stale => CurrentMachineStateHttpVocabulary.Stale,
            CurrentStateUsability.Indeterminate => CurrentMachineStateHttpVocabulary.Indeterminate,
            CurrentStateUsability.Behind => CurrentMachineStateHttpVocabulary.CoverageBehind,
            _ => throw new InvalidOperationException(
                "Unsupported current-state usability value."),
        };
}
