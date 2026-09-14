using FactoryConnect.Abstractions;

namespace FactoryConnect.Infrastructure;

public sealed class InMemoryCurrentStateAuthorityCutProvider :
    ICurrentStateAuthorityCutProvider
{
    private const int MaximumAttempts = 3;

    private readonly IObservationIngestionStore _observationStore;
    private readonly IMappingCoverageAuthorityStore _mappingStore;
    private readonly IMachineStateActivityAuthorityStore _stateActivityStore;
    private readonly Action<int>? _interleave;

    public InMemoryCurrentStateAuthorityCutProvider(
        IObservationIngestionStore observationStore,
        IMappingCoverageAuthorityStore mappingStore,
        IMachineStateActivityAuthorityStore stateActivityStore)
        : this(observationStore, mappingStore, stateActivityStore, null)
    {
    }

    internal InMemoryCurrentStateAuthorityCutProvider(
        IObservationIngestionStore observationStore,
        IMappingCoverageAuthorityStore mappingStore,
        IMachineStateActivityAuthorityStore stateActivityStore,
        Action<int>? interleave)
    {
        ArgumentNullException.ThrowIfNull(observationStore);
        ArgumentNullException.ThrowIfNull(mappingStore);
        ArgumentNullException.ThrowIfNull(stateActivityStore);

        _observationStore = observationStore;
        _mappingStore = mappingStore;
        _stateActivityStore = stateActivityStore;
        _interleave = interleave;
    }

    public async Task<CurrentStateAuthorityCutReadResult> ReadAuthorityCutAsync(
        CurrentStateAuthorityBinding binding,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        cancellationToken.ThrowIfCancellationRequested();

        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            var first = await CollectAsync(binding, cancellationToken)
                .ConfigureAwait(false);

            _interleave?.Invoke(attempt);
            cancellationToken.ThrowIfCancellationRequested();

            var second = await CollectAsync(binding, cancellationToken)
                .ConfigureAwait(false);

            if (!IsConsistent(binding, first) ||
                !IsConsistent(binding, second))
            {
                return StableCurrentStateAuthorityCutUnavailable.Instance;
            }

            if (HasSameAuthorityIdentities(first, second))
            {
                return new StableCurrentStateAuthorityCut(
                    new CurrentStateAuthorityCut(
                        binding,
                        second.Acquisition,
                        second.Mapping,
                        second.Evaluation));
            }
        }

        return StableCurrentStateAuthorityCutUnavailable.Instance;
    }

    private async ValueTask<AuthorityCollection> CollectAsync(
        CurrentStateAuthorityBinding binding,
        CancellationToken cancellationToken)
    {
        var acquisition = await _observationStore
            .ReadAcquisitionContactAuthorityAsync(
                binding.ObservationStreamId,
                cancellationToken)
            .ConfigureAwait(false);
        var mapping = await _mappingStore
            .ReadAsync(
                binding.MappingProcessorId,
                binding.ObservationStreamId,
                cancellationToken)
            .ConfigureAwait(false);
        var stateActivity = await _stateActivityStore
            .ReadAsync(
                binding.StateProcessorId,
                binding.ObservationStreamId,
                cancellationToken)
            .ConfigureAwait(false);

        return new AuthorityCollection(
            acquisition,
            mapping,
            stateActivity?.EvaluationAuthority);
    }

    private static bool IsConsistent(
        CurrentStateAuthorityBinding binding,
        AuthorityCollection collection)
    {
        var acquisition = collection.Acquisition;
        if (acquisition is not null &&
            acquisition.ObservationStreamId != binding.ObservationStreamId)
        {
            return false;
        }

        var mapping = collection.Mapping;
        if (mapping is not null &&
            (mapping.MappingProcessorId != binding.MappingProcessorId ||
             mapping.ObservationStreamId != binding.ObservationStreamId ||
             (mapping.MappedEvaluationInputHighWater is not null &&
              mapping.MappedEvaluationInputHighWater > mapping.RawConsumedThrough)))
        {
            return false;
        }

        var evaluation = collection.Evaluation;
        if (evaluation is not null &&
            (evaluation.StateProcessorId != binding.StateProcessorId ||
             evaluation.ObservationStreamId != binding.ObservationStreamId))
        {
            return false;
        }

        if (acquisition is not null &&
            mapping is not null &&
            (acquisition.RawAcceptedThrough is null ||
             mapping.RawConsumedThrough > acquisition.RawAcceptedThrough))
        {
            return false;
        }

        return mapping is null ||
               evaluation is null ||
               (mapping.MappedEvaluationInputHighWater is not null &&
                evaluation.EvaluatedThrough <=
                    mapping.MappedEvaluationInputHighWater);
    }

    private static bool HasSameAuthorityIdentities(
        AuthorityCollection first,
        AuthorityCollection second) =>
        SameAcquisition(first.Acquisition, second.Acquisition) &&
        SameMapping(first.Mapping, second.Mapping) &&
        SameEvaluation(first.Evaluation, second.Evaluation);

    private static bool SameAcquisition(
        AcquisitionContactAuthority? first,
        AcquisitionContactAuthority? second) =>
        first is null
            ? second is null
            : second is not null &&
              first.ObservationStreamId == second.ObservationStreamId &&
              first.AcquisitionRevision == second.AcquisitionRevision;

    private static bool SameMapping(
        MappingCoverageAuthority? first,
        MappingCoverageAuthority? second) =>
        first is null
            ? second is null
            : second is not null &&
              first.MappingProcessorId == second.MappingProcessorId &&
              first.ObservationStreamId == second.ObservationStreamId &&
              first.MappingRevision == second.MappingRevision;

    private static bool SameEvaluation(
        EvaluationAuthority? first,
        EvaluationAuthority? second) =>
        first is null
            ? second is null
            : second is not null &&
              first.StateProcessorId == second.StateProcessorId &&
              first.ObservationStreamId == second.ObservationStreamId &&
              first.ProjectionRevision == second.ProjectionRevision;

    private readonly record struct AuthorityCollection(
        AcquisitionContactAuthority? Acquisition,
        MappingCoverageAuthority? Mapping,
        EvaluationAuthority? Evaluation);
}
