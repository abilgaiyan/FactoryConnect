using FactoryConnect.Abstractions;

namespace FactoryConnect.Infrastructure;

public sealed class InMemoryObservationIngestionStore :
    IObservationIngestionStore,
    IDurableObservationReader,
    IObservationProcessingCheckpointStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<ObservationStreamId, ObservationCheckpoint> _checkpoints = [];
    private readonly Dictionary<ObservationKey, DurableMachineObservation> _observations = [];
    private readonly Dictionary<ObservationStreamId, ObservationPosition> _lastPositions = [];
    private readonly Dictionary<ObservationStreamId, AcquisitionContactAuthority> _acquisitionAuthorities = [];
    private readonly Dictionary<ObservationStreamId, ulong> _lastAcquisitionRevisionValues = [];
    private readonly Dictionary<ProcessingCheckpointKey, ObservationProcessingCheckpoint> _processingCheckpoints = [];

    public ValueTask<ObservationCheckpoint?> ReadCheckpointAsync(
        ObservationStreamId streamId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(streamId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _checkpoints.TryGetValue(streamId, out var checkpoint);
            return ValueTask.FromResult<ObservationCheckpoint?>(checkpoint);
        }
    }

    public ValueTask<AcquisitionContactAuthority?> ReadAcquisitionContactAuthorityAsync(
        ObservationStreamId streamId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(streamId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _acquisitionAuthorities.TryGetValue(streamId, out var authority);
            return ValueTask.FromResult<AcquisitionContactAuthority?>(authority);
        }
    }

    public ValueTask CommitAsync(
        ObservationIngestionBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ValidateBatchShape(batch);

            _checkpoints.TryGetValue(batch.Checkpoint.StreamId, out var currentCheckpoint);
            var casAuthorized = currentCheckpoint == batch.ExpectedCheckpoint;

            if (!casAuthorized)
            {
                ValidateExactCurrentStateReplay(batch, currentCheckpoint);
                return ValueTask.CompletedTask;
            }

            ValidateCheckpointTransition(batch, currentCheckpoint);
            var pending = StageObservations(batch);
            var staged = AssignPositions(batch, pending);

            if (currentCheckpoint == batch.Checkpoint && staged.Length > 0)
            {
                throw new InvalidOperationException(
                    "A same-checkpoint acquisition cannot add new observations.");
            }

            var resultingRawAcceptedThrough = staged.Length > 0
                ? staged[^1].Position
                : _lastPositions.TryGetValue(batch.Checkpoint.StreamId, out var lastPosition)
                    ? lastPosition
                    : null;

            var authorityPlan = StageAuthority(
                batch,
                resultingRawAcceptedThrough);

            foreach (var observation in staged)
            {
                var key = new ObservationKey(
                    observation.StreamId,
                    observation.InstanceId,
                    observation.Sequence);
                _observations.Add(key, observation);
            }

            if (staged.Length > 0)
            {
                _lastPositions[batch.Checkpoint.StreamId] = staged[^1].Position;
            }

            _checkpoints[batch.Checkpoint.StreamId] = batch.Checkpoint;
            _acquisitionAuthorities[batch.Checkpoint.StreamId] = authorityPlan.Authority;

            if (authorityPlan.AdvanceAllocator)
            {
                _lastAcquisitionRevisionValues[batch.Checkpoint.StreamId] =
                    authorityPlan.Authority.AcquisitionRevision.Value;
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<ObservationReadBatch> ReadAsync(
        ObservationReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var readLimit = request.BatchSize == int.MaxValue
                ? int.MaxValue
                : request.BatchSize + 1;
            var observations = _observations.Values
                .Where(observation =>
                    observation.StreamId == request.StreamId &&
                    (request.AfterPosition is null || observation.Position > request.AfterPosition))
                .OrderBy(observation => observation.Position)
                .Take(readLimit)
                .ToArray();
            var hasMore = observations.Length > request.BatchSize;
            var page = hasMore ? observations[..request.BatchSize] : observations;

            return ValueTask.FromResult(
                new ObservationReadBatch(request.StreamId, page, hasMore));
        }
    }

    public ValueTask<ObservationProcessingCheckpoint?> ReadCheckpointAsync(
        ObservationProcessorId processorId,
        ObservationStreamId streamId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(streamId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _processingCheckpoints.TryGetValue(
                new ProcessingCheckpointKey(processorId, streamId),
                out var checkpoint);
            return ValueTask.FromResult<ObservationProcessingCheckpoint?>(checkpoint);
        }
    }

    public ValueTask CommitAsync(
        ObservationProcessingCommit commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var checkpoint = commit.Checkpoint;
            var key = new ProcessingCheckpointKey(
                checkpoint.ProcessorId,
                checkpoint.StreamId);

            _processingCheckpoints.TryGetValue(key, out var current);
            if (current != commit.ExpectedCheckpoint)
            {
                throw new InvalidOperationException(
                    "The processing checkpoint no longer matches the expected state.");
            }

            if (!_observations.Values.Any(observation =>
                    observation.StreamId == checkpoint.StreamId &&
                    observation.Position == checkpoint.Position))
            {
                throw new InvalidOperationException(
                    "A processing checkpoint must reference a durable observation in the same stream.");
            }

            _processingCheckpoints[key] = checkpoint;
        }

        return ValueTask.CompletedTask;
    }

    public SequencedMachineObservation[] ReadObservations(
        ObservationStreamId streamId)
    {
        ArgumentNullException.ThrowIfNull(streamId);

        lock (_gate)
        {
            return _observations.Values
                .Where(observation => observation.StreamId == streamId)
                .OrderBy(observation => observation.Position)
                .Select(observation =>
                    new SequencedMachineObservation(
                        observation.Sequence,
                        observation.Observation))
                .ToArray();
        }
    }

    private AuthorityPlan StageAuthority(
        ObservationIngestionBatch batch,
        ObservationPosition? rawAcceptedThrough)
    {
        var streamId = batch.Checkpoint.StreamId;
        _acquisitionAuthorities.TryGetValue(streamId, out var current);

        if (current is not null &&
            SameInstant(current.SuccessfulContactTime, batch.SuccessfulContactTime) &&
            current.RawAcceptedThrough == rawAcceptedThrough)
        {
            return new AuthorityPlan(current, false);
        }

        ulong revisionValue;
        if (_lastAcquisitionRevisionValues.TryGetValue(streamId, out var lastRevision))
        {
            if (lastRevision == ulong.MaxValue)
            {
                throw new InvalidOperationException(
                    "The acquisition authority revision is exhausted for this stream.");
            }

            revisionValue = lastRevision + 1;
        }
        else
        {
            if (current is not null)
            {
                throw new InvalidOperationException(
                    "The acquisition authority revision allocator is inconsistent.");
            }

            revisionValue = 0;
        }

        return new AuthorityPlan(
            new AcquisitionContactAuthority(
                streamId,
                batch.SuccessfulContactTime,
                rawAcceptedThrough,
                new AcquisitionAuthorityRevision(revisionValue)),
            true);
    }

    private void ValidateExactCurrentStateReplay(
        ObservationIngestionBatch batch,
        ObservationCheckpoint? currentCheckpoint)
    {
        if (currentCheckpoint is null || currentCheckpoint != batch.Checkpoint)
        {
            throw new InvalidOperationException(
                "The durable checkpoint no longer matches the expected state.");
        }

        var pending = StageObservations(batch);
        if (pending.Any(item => !_observations.ContainsKey(item.Key)))
        {
            throw new InvalidOperationException(
                "A stale replay cannot add observations to the current committed state.");
        }

        if (!_acquisitionAuthorities.TryGetValue(
                batch.Checkpoint.StreamId,
                out var authority))
        {
            throw new InvalidOperationException(
                "A stale replay requires an existing acquisition authority.");
        }

        var rawAcceptedThrough = _lastPositions.TryGetValue(
            batch.Checkpoint.StreamId,
            out var lastPosition)
            ? lastPosition
            : null;

        if (!SameInstant(authority.SuccessfulContactTime, batch.SuccessfulContactTime) ||
            authority.RawAcceptedThrough != rawAcceptedThrough)
        {
            throw new InvalidOperationException(
                "The stale replay does not reproduce the current committed acquisition authority.");
        }
    }

    private DurableMachineObservation[] AssignPositions(
        ObservationIngestionBatch batch,
        StagedObservation[] pending)
    {
        var newObservations = pending
            .Where(item => !_observations.ContainsKey(item.Key))
            .ToArray();

        if (newObservations.Length == 0)
        {
            return [];
        }

        var lastPosition = _lastPositions.TryGetValue(
            batch.Checkpoint.StreamId,
            out var current)
            ? current.Value
            : 0;
        var result = new DurableMachineObservation[newObservations.Length];

        for (var index = 0; index < newObservations.Length; index++)
        {
            var item = newObservations[index];
            var position = new ObservationPosition(
                checked(lastPosition + (ulong)index + 1));

            result[index] = new DurableMachineObservation(
                position,
                batch.Checkpoint.StreamId,
                batch.Checkpoint.InstanceId,
                item.Observation.Sequence,
                item.Observation.Observation);
        }

        return result;
    }

    private StagedObservation[] StageObservations(
        ObservationIngestionBatch batch)
    {
        Dictionary<ObservationKey, SequencedMachineObservation> pending = [];
        List<StagedObservation> ordered = [];

        foreach (var item in batch.Observations)
        {
            var key = new ObservationKey(
                batch.Checkpoint.StreamId,
                batch.Checkpoint.InstanceId,
                item.Sequence);

            if ((_observations.TryGetValue(key, out var existing) &&
                 (existing.Sequence != item.Sequence || existing.Observation != item.Observation)) ||
                (pending.TryGetValue(key, out var staged) && staged != item))
            {
                throw new InvalidOperationException(
                    "The stream already contains a different observation at the same instance and sequence.");
            }

            if (pending.TryAdd(key, item))
            {
                ordered.Add(new StagedObservation(key, item));
            }
        }

        return ordered.ToArray();
    }

    private void ValidateCheckpointTransition(
        ObservationIngestionBatch batch,
        ObservationCheckpoint? current)
    {
        if (current is not null &&
            current.InstanceId == batch.Checkpoint.InstanceId &&
            batch.Checkpoint.NextSequence < current.NextSequence)
        {
            throw new InvalidOperationException(
                "A checkpoint cannot move backwards within an Agent instance.");
        }
    }

    private static void ValidateBatchShape(ObservationIngestionBatch batch)
    {
        foreach (var item in batch.Observations)
        {
            if (item.Observation.MachineId != batch.Checkpoint.StreamId.MachineId)
            {
                throw new InvalidOperationException(
                    "Every observation must belong to the checkpoint machine.");
            }

            if (item.Sequence >= batch.Checkpoint.NextSequence)
            {
                throw new InvalidOperationException(
                    "Every observation sequence must precede the checkpoint.");
            }
        }
    }

    private static bool SameInstant(DateTimeOffset left, DateTimeOffset right) =>
        left.UtcDateTime.Ticks == right.UtcDateTime.Ticks;

    private readonly record struct AuthorityPlan(
        AcquisitionContactAuthority Authority,
        bool AdvanceAllocator);

    private readonly record struct StagedObservation(
        ObservationKey Key,
        SequencedMachineObservation Observation);

    private readonly record struct ObservationKey(
        ObservationStreamId StreamId,
        ulong InstanceId,
        ulong Sequence);

    private readonly record struct ProcessingCheckpointKey(
        ObservationProcessorId ProcessorId,
        ObservationStreamId StreamId);
}
