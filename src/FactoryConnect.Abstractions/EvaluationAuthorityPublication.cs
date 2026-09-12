namespace FactoryConnect.Abstractions;

/// <summary>
/// Identifies one logical evaluation-authority publication independently of
/// provider-owned publication revision allocation.
/// </summary>
public sealed record EvaluationAuthorityReplayIdentity
{
    public EvaluationAuthorityReplayIdentity(
        ObservationProcessorId stateProcessorId,
        ObservationStreamId observationStreamId,
        ObservationPosition evaluatedThrough,
        MachineState machineState,
        ulong lastConsumedInstanceId,
        CurrentStatePolicyReference appliedContinuityPolicy)
    {
        ArgumentNullException.ThrowIfNull(stateProcessorId);
        ArgumentNullException.ThrowIfNull(observationStreamId);
        ArgumentNullException.ThrowIfNull(evaluatedThrough);
        ArgumentNullException.ThrowIfNull(appliedContinuityPolicy);

        if (!Enum.IsDefined(machineState))
        {
            throw new ArgumentOutOfRangeException(nameof(machineState));
        }

        StateProcessorId = stateProcessorId;
        ObservationStreamId = observationStreamId;
        EvaluatedThrough = evaluatedThrough;
        MachineState = machineState;
        LastConsumedInstanceId = lastConsumedInstanceId;
        AppliedContinuityPolicy = appliedContinuityPolicy;
    }

    public ObservationProcessorId StateProcessorId { get; }

    public ObservationStreamId ObservationStreamId { get; }

    public ObservationPosition EvaluatedThrough { get; }

    public MachineState MachineState { get; }

    public ulong LastConsumedInstanceId { get; }

    public CurrentStatePolicyReference AppliedContinuityPolicy { get; }
}

/// <summary>
/// Proposes one evaluation-authority publication. The caller supplies the
/// authority revision it observed before evaluation, while the provider owns
/// allocation of the revision assigned to a successful new publication.
/// </summary>
public sealed record EvaluationAuthorityPublication
{
    public EvaluationAuthorityPublication(
        StateProjectionAuthorityRevision? expectedRevision,
        ObservationProcessorId stateProcessorId,
        ObservationStreamId observationStreamId,
        ObservationPosition evaluatedThrough,
        MachineState machineState,
        ulong lastConsumedInstanceId,
        CurrentStatePolicyReference appliedContinuityPolicy)
    {
        ExpectedRevision = expectedRevision;
        ReplayIdentity = new EvaluationAuthorityReplayIdentity(
            stateProcessorId,
            observationStreamId,
            evaluatedThrough,
            machineState,
            lastConsumedInstanceId,
            appliedContinuityPolicy);
    }

    /// <summary>
    /// The provider-owned revision observed before the logical evaluation.
    /// <see langword="null"/> means no authority existed when evaluation began.
    /// This value participates in compare-and-swap but not replay identity.
    /// </summary>
    public StateProjectionAuthorityRevision? ExpectedRevision { get; }

    /// <summary>
    /// Deterministic semantic identity of the logical publication. Exact replay
    /// requires equality of every member of this identity.
    /// </summary>
    public EvaluationAuthorityReplayIdentity ReplayIdentity { get; }

    public ObservationProcessorId StateProcessorId => ReplayIdentity.StateProcessorId;

    public ObservationStreamId ObservationStreamId => ReplayIdentity.ObservationStreamId;

    public ObservationPosition EvaluatedThrough => ReplayIdentity.EvaluatedThrough;

    public MachineState MachineState => ReplayIdentity.MachineState;

    public ulong LastConsumedInstanceId => ReplayIdentity.LastConsumedInstanceId;

    public CurrentStatePolicyReference AppliedContinuityPolicy =>
        ReplayIdentity.AppliedContinuityPolicy;
}

public enum EvaluationAuthorityPublicationDisposition
{
    NewPublication = 0,
    ExactReplay = 1,
}

/// <summary>
/// Closed result algebra for evaluation-authority publication.
/// </summary>
public abstract record EvaluationAuthorityPublicationResult;

/// <summary>
/// A publication was accepted. Exact replay returns the previously allocated
/// authority and revision rather than allocating another revision.
/// </summary>
public sealed record EvaluationAuthorityPublicationAccepted :
    EvaluationAuthorityPublicationResult
{
    public EvaluationAuthorityPublicationAccepted(
        EvaluationAuthority authority,
        EvaluationAuthorityPublicationDisposition disposition)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }

        Authority = authority;
        Disposition = disposition;
    }

    public EvaluationAuthority Authority { get; }

    public EvaluationAuthorityPublicationDisposition Disposition { get; }
}

/// <summary>
/// The expected authority revision is stale or otherwise contradictory to the
/// currently published authority. No mutation is permitted for this result.
/// </summary>
public sealed record EvaluationAuthorityPublicationConflict :
    EvaluationAuthorityPublicationResult
{
    public EvaluationAuthorityPublicationConflict(EvaluationAuthority? currentAuthority)
    {
        CurrentAuthority = currentAuthority;
    }

    public EvaluationAuthority? CurrentAuthority { get; }
}

/// <summary>
/// A changed publication could not allocate another provider-owned revision.
/// The current authority remains unchanged. Exact replay remains legal even
/// when the revision space is exhausted.
/// </summary>
public sealed record EvaluationAuthorityRevisionExhausted :
    EvaluationAuthorityPublicationResult
{
    public EvaluationAuthorityRevisionExhausted(EvaluationAuthority currentAuthority)
    {
        ArgumentNullException.ThrowIfNull(currentAuthority);
        CurrentAuthority = currentAuthority;
    }

    public EvaluationAuthority CurrentAuthority { get; }
}
