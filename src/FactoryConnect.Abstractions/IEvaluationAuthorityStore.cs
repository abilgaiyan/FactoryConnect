namespace FactoryConnect.Abstractions;

/// <summary>
/// Stores authoritative state-evaluation publication state independently for
/// each state processor and observation stream.
/// </summary>
/// <remarks>
/// This interface defines the portable evaluation-authority operation algebra.
/// It does not authorize an independent post-projection side write from the
/// state/activity processor. FC-031 state/activity integration must publish the
/// FC-024 projection, derived outputs, and the accepted FC-031 evaluation
/// authority within one provider-owned atomic publication boundary.
/// </remarks>
public interface IEvaluationAuthorityStore
{
    /// <summary>
    /// Reads the current evaluation authority, or <see langword="null"/> when
    /// no mapped durable input has yet been incorporated by the state processor
    /// for the stream.
    /// </summary>
    ValueTask<EvaluationAuthority?> ReadAsync(
        ObservationProcessorId stateProcessorId,
        ObservationStreamId observationStreamId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes one logical evaluation-authority transition using
    /// compare-and-swap against the provider-owned revision observed before
    /// evaluation.
    /// </summary>
    /// <remarks>
    /// A successful changed publication allocates exactly one provider-owned
    /// <see cref="StateProjectionAuthorityRevision"/>. The initial changed
    /// publication allocates revision zero; later changed publications advance
    /// monotonically by one.
    ///
    /// Exact replay is determined by
    /// <see cref="EvaluationAuthorityPublication.ReplayIdentity"/>. When the
    /// currently published authority represents that same logical publication,
    /// replay succeeds even when the supplied expected revision is stale and
    /// returns the previously committed authority/revision without allocating a
    /// new revision. A publication superseded by later evaluation is not an
    /// exact replay of current authority and is conflicting.
    ///
    /// A stale or contradictory publication returns
    /// <see cref="EvaluationAuthorityPublicationConflict"/> without mutation.
    /// Revision exhaustion returns
    /// <see cref="EvaluationAuthorityRevisionExhausted"/> without mutation;
    /// exact replay remains legal at the exhausted revision.
    ///
    /// No invocation that represents no new evaluation may create revision
    /// churn. A recovery retry may publish only an already-determined logical
    /// publication represented by the same deterministic replay identity.
    /// </remarks>
    ValueTask<EvaluationAuthorityPublicationResult> PublishAsync(
        EvaluationAuthorityPublication publication,
        CancellationToken cancellationToken = default);
}
