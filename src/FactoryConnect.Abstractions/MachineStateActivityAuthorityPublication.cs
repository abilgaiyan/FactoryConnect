namespace FactoryConnect.Abstractions;

public sealed record MachineStateActivityAuthoritySnapshot
{
    public MachineStateActivityAuthoritySnapshot(
        MachineStateActivityProjection projection,
        EvaluationAuthority evaluationAuthority)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(evaluationAuthority);

        if (projection.ProcessorId != evaluationAuthority.StateProcessorId ||
            projection.StreamId != evaluationAuthority.ObservationStreamId ||
            projection.Position != evaluationAuthority.EvaluatedThrough ||
            projection.State != evaluationAuthority.MachineState)
        {
            throw new ArgumentException(
                "Projection and evaluation authority must represent one coherent state.",
                nameof(evaluationAuthority));
        }

        Projection = projection;
        EvaluationAuthority = evaluationAuthority;
    }

    public MachineStateActivityProjection Projection { get; }

    public EvaluationAuthority EvaluationAuthority { get; }
}

public sealed record MachineStateActivityAuthorityPublication
{
    public MachineStateActivityAuthorityPublication(
        ObservationPosition? expectedProjectionPosition,
        StateProjectionAuthorityRevision? expectedAuthorityRevision,
        MachineStateActivityProjection projection,
        IReadOnlyList<DurableMachineStateChangedEvent> stateChanges,
        IReadOnlyList<DurableMachineActivityPeriod> activityPeriods,
        EvaluationAuthorityReplayIdentity evaluationIdentity)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(stateChanges);
        ArgumentNullException.ThrowIfNull(activityPeriods);
        ArgumentNullException.ThrowIfNull(evaluationIdentity);

        ValidateIntrinsic(
            projection,
            stateChanges,
            activityPeriods,
            evaluationIdentity);

        ExpectedProjectionPosition = expectedProjectionPosition;
        ExpectedAuthorityRevision = expectedAuthorityRevision;
        Projection = projection;
        StateChanges = stateChanges.ToArray();
        ActivityPeriods = activityPeriods.ToArray();
        EvaluationIdentity = evaluationIdentity;
    }

    public ObservationPosition? ExpectedProjectionPosition { get; }

    public StateProjectionAuthorityRevision? ExpectedAuthorityRevision { get; }

    public MachineStateActivityProjection Projection { get; }

    public IReadOnlyList<DurableMachineStateChangedEvent> StateChanges { get; }

    public IReadOnlyList<DurableMachineActivityPeriod> ActivityPeriods { get; }

    public EvaluationAuthorityReplayIdentity EvaluationIdentity { get; }

    private static void ValidateIntrinsic(
        MachineStateActivityProjection projection,
        IReadOnlyList<DurableMachineStateChangedEvent> stateChanges,
        IReadOnlyList<DurableMachineActivityPeriod> activityPeriods,
        EvaluationAuthorityReplayIdentity evaluationIdentity)
    {
        if (projection.ProcessorId != evaluationIdentity.StateProcessorId ||
            projection.StreamId != evaluationIdentity.ObservationStreamId ||
            projection.Position != evaluationIdentity.EvaluatedThrough ||
            projection.State != evaluationIdentity.MachineState)
        {
            throw new ArgumentException(
                "Projection and evaluation identity must be intrinsically coherent.",
                nameof(evaluationIdentity));
        }

        ValidateStateChanges(projection, stateChanges);
        ValidateActivityPeriods(projection, activityPeriods);
    }

    private static void ValidateStateChanges(
        MachineStateActivityProjection projection,
        IReadOnlyList<DurableMachineStateChangedEvent> stateChanges)
    {
        ObservationPosition? previous = null;
        foreach (var item in stateChanges)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (item.ProcessorId != projection.ProcessorId ||
                item.StreamId != projection.StreamId ||
                item.Position > projection.Position)
            {
                throw new ArgumentException(
                    "State changes must belong to the target projection and may not extend beyond it.",
                    nameof(stateChanges));
            }

            if (previous is not null && item.Position <= previous)
            {
                throw new ArgumentException(
                    "State changes must be in deterministic strictly increasing durable-position order.",
                    nameof(stateChanges));
            }

            previous = item.Position;
        }
    }

    private static void ValidateActivityPeriods(
        MachineStateActivityProjection projection,
        IReadOnlyList<DurableMachineActivityPeriod> activityPeriods)
    {
        ObservationPosition? previous = null;
        foreach (var item in activityPeriods)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (item.ProcessorId != projection.ProcessorId ||
                item.StreamId != projection.StreamId ||
                item.Position > projection.Position)
            {
                throw new ArgumentException(
                    "Activity periods must belong to the target projection and may not extend beyond it.",
                    nameof(activityPeriods));
            }

            if (previous is not null && item.Position <= previous)
            {
                throw new ArgumentException(
                    "Activity periods must be in deterministic strictly increasing durable-position order.",
                    nameof(activityPeriods));
            }

            previous = item.Position;
        }
    }
}

public abstract record MachineStateActivityAuthorityPublicationResult;

public sealed record MachineStateActivityAuthorityPublicationAccepted :
    MachineStateActivityAuthorityPublicationResult
{
    public MachineStateActivityAuthorityPublicationAccepted(
        MachineStateActivityAuthoritySnapshot snapshot,
        EvaluationAuthorityPublicationDisposition disposition)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }

        Snapshot = snapshot;
        Disposition = disposition;
    }

    public MachineStateActivityAuthoritySnapshot Snapshot { get; }

    public EvaluationAuthorityPublicationDisposition Disposition { get; }
}

public sealed record MachineStateActivityAuthorityPublicationConflict :
    MachineStateActivityAuthorityPublicationResult
{
    public MachineStateActivityAuthorityPublicationConflict(
        MachineStateActivityAuthoritySnapshot? current)
    {
        Current = current;
    }

    public MachineStateActivityAuthoritySnapshot? Current { get; }
}

public sealed record MachineStateActivityAuthorityRevisionExhausted :
    MachineStateActivityAuthorityPublicationResult
{
    public MachineStateActivityAuthorityRevisionExhausted(
        MachineStateActivityAuthoritySnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);
        Current = current;
    }

    public MachineStateActivityAuthoritySnapshot Current { get; }
}

public interface IMachineStateActivityAuthorityStore
{
    ValueTask<MachineStateActivityAuthoritySnapshot?> ReadAsync(
        ObservationProcessorId stateProcessorId,
        ObservationStreamId observationStreamId,
        CancellationToken cancellationToken = default);

    ValueTask<MachineStateActivityAuthorityPublicationResult> PublishAsync(
        MachineStateActivityAuthorityPublication publication,
        CancellationToken cancellationToken = default);
}
