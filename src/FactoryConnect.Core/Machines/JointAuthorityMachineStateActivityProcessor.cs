using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Machines;

/// <summary>
/// Publishes valid joint state/activity proposals and returns chronology holds
/// without calling the authority writer.
/// </summary>
public sealed class JointAuthorityMachineStateActivityProcessor :
    IMachineStateActivityOutcomeProcessor
{
    private readonly IMachineStateActivityAuthorityStore _store;
    private readonly CurrentStateContinuityPolicy _policy;
    private readonly MachineStateActivityContinuityCalculator _calculator;

    public JointAuthorityMachineStateActivityProcessor(
        ObservationProcessorId processorId,
        IMachineStateActivityAuthorityStore store,
        CurrentStateContinuityPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(policy);

        ProcessorId = processorId;
        _store = store;
        _policy = policy;
        _calculator = new MachineStateActivityContinuityCalculator(processorId);
    }

    public ObservationProcessorId ProcessorId { get; }

    public async ValueTask ProcessAsync(
        IReadOnlyList<DurableMappedMachineObservation> observations,
        CancellationToken cancellationToken = default)
    {
        var result = await ProcessWithOutcomeAsync(observations, cancellationToken);
        if (result is MachineStateActivityChronologyHold hold)
        {
            // Legacy void callers must never interpret a hold as accepted processing.
            throw new InvalidOperationException(hold.ToDiagnosticString());
        }
    }

    public async ValueTask<MachineStateActivityContinuityCalculationResult> ProcessWithOutcomeAsync(
        IReadOnlyList<DurableMappedMachineObservation> observations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observations);
        cancellationToken.ThrowIfCancellationRequested();

        if (observations.Count == 0)
        {
            return MachineStateActivityContinuityNoAdvance.Instance;
        }

        var first = observations[0] ??
            throw new ArgumentException("Observations may not contain null entries.", nameof(observations));
        var current = await _store.ReadAsync(
            ProcessorId,
            first.StreamId,
            cancellationToken);

        var calculation = _calculator.Calculate(current, observations, _policy);
        if (calculation is MachineStateActivityContinuityNoAdvance or MachineStateActivityChronologyHold)
        {
            return calculation;
        }

        var proposal = ((MachineStateActivityContinuityPublication)calculation).Publication;
        var result = await _store.PublishAsync(proposal, cancellationToken);

        switch (result)
        {
            case MachineStateActivityAuthorityPublicationAccepted:
                return calculation;
            case MachineStateActivityAuthorityPublicationConflict:
                throw new InvalidOperationException(
                    "Joint state/activity authority publication conflicted with the current authority.");
            case MachineStateActivityAuthorityRevisionExhausted:
                throw new InvalidOperationException(
                    "Joint state/activity authority revision space is exhausted.");
            default:
                throw new InvalidOperationException(
                    "Joint state/activity authority provider returned an unsupported result.");
        }
    }
}
