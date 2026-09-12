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
    ValueTask CommitAsync(
        MappingCoverageCommit commit,
        CancellationToken cancellationToken = default);
}
