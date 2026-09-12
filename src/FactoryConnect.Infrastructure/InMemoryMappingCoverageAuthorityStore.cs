using FactoryConnect.Abstractions;

namespace FactoryConnect.Infrastructure;

public sealed class InMemoryMappingCoverageAuthorityStore :
    IMappingCoverageAuthorityStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<MappingAuthorityKey, MappingCoverageAuthority> _authorities = [];

    public ValueTask<MappingCoverageAuthority?> ReadAsync(
        ObservationProcessorId mappingProcessorId,
        ObservationStreamId observationStreamId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mappingProcessorId);
        ArgumentNullException.ThrowIfNull(observationStreamId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _authorities.TryGetValue(
                new MappingAuthorityKey(mappingProcessorId, observationStreamId),
                out var authority);
            return ValueTask.FromResult<MappingCoverageAuthority?>(authority);
        }
    }

    public ValueTask CommitAsync(
        MappingCoverageCommit commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var key = new MappingAuthorityKey(
                commit.MappingProcessorId,
                commit.ObservationStreamId);
            _authorities.TryGetValue(key, out var current);

            if (current != commit.ExpectedAuthority)
            {
                if (current is not null && SameCoverage(current, commit))
                {
                    return ValueTask.CompletedTask;
                }

                throw new InvalidOperationException(
                    "The mapping coverage authority no longer matches the expected state.");
            }

            if (current is not null && SameCoverage(current, commit))
            {
                return ValueTask.CompletedTask;
            }

            var revision = current is null
                ? 0UL
                : NextRevision(current.MappingRevision);

            _authorities[key] = new MappingCoverageAuthority(
                commit.MappingProcessorId,
                commit.ObservationStreamId,
                commit.RawConsumedThrough,
                commit.MappedEvaluationInputHighWater,
                new MappingAuthorityRevision(revision));
        }

        return ValueTask.CompletedTask;
    }

    private static ulong NextRevision(MappingAuthorityRevision current)
    {
        if (current.Value == ulong.MaxValue)
        {
            throw new InvalidOperationException(
                "The mapping authority revision space is exhausted.");
        }

        return current.Value + 1;
    }

    private static bool SameCoverage(
        MappingCoverageAuthority current,
        MappingCoverageCommit proposed) =>
        current.MappingProcessorId == proposed.MappingProcessorId &&
        current.ObservationStreamId == proposed.ObservationStreamId &&
        current.RawConsumedThrough == proposed.RawConsumedThrough &&
        current.MappedEvaluationInputHighWater == proposed.MappedEvaluationInputHighWater;

    private readonly record struct MappingAuthorityKey(
        ObservationProcessorId MappingProcessorId,
        ObservationStreamId ObservationStreamId);
}
