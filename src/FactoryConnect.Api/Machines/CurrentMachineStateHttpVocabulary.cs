namespace FactoryConnect.Api.Machines;

internal static class CurrentMachineStateHttpVocabulary
{
    public const string OutcomeEvidence = "evidence";
    public const string OutcomeNoEvidence = "no-evidence";

    public const string CoverageComplete = "complete";
    public const string CoverageBehind = "behind";
    public const string Indeterminate = "indeterminate";

    public const string MachineStateUnknown = "unknown";
    public const string MachineStateStopped = "stopped";
    public const string MachineStateIdle = "idle";
    public const string MachineStateRunning = "running";
    public const string MachineStateFault = "fault";

    public const string Current = "current";
    public const string Stale = "stale";

    public static IReadOnlyList<string> Outcomes { get; } =
        [OutcomeEvidence, OutcomeNoEvidence];

    public static IReadOnlyList<string> Coverages { get; } =
        [CoverageComplete, CoverageBehind, Indeterminate];

    public static IReadOnlyList<string> MachineStates { get; } =
        [MachineStateUnknown, MachineStateStopped, MachineStateIdle, MachineStateRunning, MachineStateFault];

    public static IReadOnlyList<string> FreshnessValues { get; } =
        [Current, Stale, Indeterminate];

    public static IReadOnlyList<string> UsabilityValues { get; } =
        [Current, Stale, Indeterminate, CoverageBehind];
}
