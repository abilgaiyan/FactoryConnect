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
    /// Publication classification is ordered.
    ///
    /// First, exact replay is determined by
    /// <see cref="EvaluationAuthorityPublication.ReplayIdentity"/>. When the
    /// currently published authority represents the same logical publication,
    /// replay succeeds before compare-and-swap validation and before revision-
    /// exhaustion validation. The supplied expected revision may therefore be
    /// stale. Exact replay returns the previously committed authority/revision,
    /// allocates no revision, performs no mutation, and remains legal when the
    /// current revision is <see cref="ulong.MaxValue"/>.
    ///
    /// Second, when the proposal is not exact replay and its expected revision
    /// does not identify current authority, publication returns
    /// <see cref="EvaluationAuthorityPublicationConflict"/> without mutation.
    ///
    /// Third, when the expected revision identifies current authority but the
    /// proposal is not strictly forward, publication also returns
    /// <see cref="EvaluationAuthorityPublicationConflict"/> without mutation.
    /// This includes a lower
    /// <see cref="EvaluationAuthorityPublication.EvaluatedThrough"/> and a
    /// same-position proposal whose machine state, last-consumed instance, or
    /// applied continuity policy differs from current authority.
    ///
    /// Fourth, a changed strictly-forward proposal that would require advancing
    /// beyond <see cref="ulong.MaxValue"/> returns
    /// <see cref="EvaluationAuthorityRevisionExhausted"/> without mutation.
    ///
    /// Otherwise the changed strictly-forward publication is accepted. It
    /// allocates exactly one provider-owned
    /// <see cref="StateProjectionAuthorityRevision"/>. The initial changed
    /// publication allocates revision zero; later accepted changed publications
    /// advance monotonically by one.
    ///
    /// A publication superseded by later evaluation is not an exact replay of
    /// current authority and is conflicting. No invocation that represents no
    /// new evaluation may create revision churn. A recovery retry may publish
    /// only an already-determined logical publication represented by the same
    /// deterministic replay identity.
    /// </remarks>
    ValueTask<EvaluationAuthorityPublicationResult> PublishAsync(
        EvaluationAuthorityPublication publication,
        CancellationToken cancellationToken = default);
}
