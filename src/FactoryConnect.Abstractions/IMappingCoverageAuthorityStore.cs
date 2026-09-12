namespace FactoryConnect.Abstractions;

/// <summary>
/// Persists authoritative mapping coverage independently for each mapping
/// processor and observation stream.
/// </summary>
public interface IMappingCoverageAuthorityStore
{
    /// <summary>
    /// Reads the current mapping coverage authority, or <see langword="null"/>
    /// when the mapping processor has not yet consumed durable raw input for
    /// the stream.
    /// </summary>
    ValueTask<MappingCoverageAuthority?> ReadAsync(
        ObservationProcessorId mappingProcessorId,
        ObservationStreamId observationStreamId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits mapping coverage using compare-and-swap semantics against the
    /// authority observed before processing the input batch.
    /// </summary>
    /// <remarks>
    /// A commit whose expected authority is stale must still succeed
    /// idempotently when the currently stored authority already represents
    /// exactly the proposed raw-consumed and mapped-input coverage. This is the
    /// at-least-once recovery case where the authority transition committed but
    /// acknowledgement was lost. A stale commit whose proposal differs from
    /// the currently stored coverage is conflicting and must be rejected.
    /// Providers own mapping-authority revision allocation; an accepted exact
    /// replay must not advance the stored revision.
    /// </remarks>
    ValueTask CommitAsync(
        MappingCoverageCommit commit,
        CancellationToken cancellationToken = default);
}
