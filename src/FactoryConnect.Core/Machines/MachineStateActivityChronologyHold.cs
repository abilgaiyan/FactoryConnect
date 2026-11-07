using System.Globalization;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Machines;

/// <summary>A rejected activity proposal. No observation in its batch is accepted by this outcome.</summary>
public sealed record MachineStateActivityChronologyHold(
    ObservationProcessorId ProcessorId,
    ObservationPosition? CommittedPosition,
    StateProjectionAuthorityRevision? CommittedRevision,
    DurableMappedMachineObservation Observation,
    MachineState ActiveState,
    DateTimeOffset ActiveStartedAt,
    MachineState ProposedState,
    CurrentStatePolicyReference AppliedPolicy) : MachineStateActivityContinuityCalculationResult
{
    public string ToDiagnosticString() => FormattableString.Invariant(
        $"Processor={ProcessorId.Value}; Machine={Observation.StreamId.MachineId}; Stream={Observation.StreamId.StreamKey}; Position={Observation.Position.Value}; Instance={Observation.InstanceId}; Sequence={Observation.Sequence}; Signal={Observation.Observation.SignalKey}; Source={Observation.Observation.Source}; Address={Observation.Observation.Address}; Type={Observation.Observation.Type}; Value={Convert.ToString(Observation.Observation.Value, CultureInfo.InvariantCulture) ?? "<null>"}; Quality={Observation.Observation.Quality}; ActiveState={ActiveState}; ActiveStartedAt={ActiveStartedAt:O}; ObservationTimestamp={Observation.Observation.Timestamp:O}; ProposedState={ProposedState}; CommittedPosition={CommittedPosition?.Value}; CommittedRevision={CommittedRevision?.Value}; Policy={AppliedPolicy}");
}

/// <summary>Explicit result path used by the activity runtime; other processor failures still propagate.</summary>
public interface IMachineStateActivityOutcomeProcessor : IMappedMachineObservationProcessor
{
    ValueTask<MachineStateActivityContinuityCalculationResult> ProcessWithOutcomeAsync(
        IReadOnlyList<DurableMappedMachineObservation> observations,
        CancellationToken cancellationToken = default);
}
