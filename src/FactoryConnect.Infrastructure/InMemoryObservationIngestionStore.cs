using FactoryConnect.Abstractions;

namespace FactoryConnect.Infrastructure;

public sealed class InMemoryObservationIngestionStore :
    IObservationIngestionStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<
        ObservationStreamId,
        ObservationCheckpoint> _checkpoints = [];
    private readonly Dictionary<
        ObservationKey,
        SequencedMachineObservation> _observations = [];

    public ValueTask<ObservationCheckpoint?> ReadCheckpointAsync(
        ObservationStreamId streamId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(streamId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _checkpoints.TryGetValue(streamId, out var checkpoint);

            return ValueTask.FromResult<ObservationCheckpoint?>(
                checkpoint);
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
            ValidateBatch(batch);
            var pending = StageObservations(batch);

            foreach (var pair in pending)
            {
                _observations.TryAdd(pair.Key, pair.Value);
            }

            _checkpoints[batch.Checkpoint.StreamId] =
                batch.Checkpoint;
        }

        return ValueTask.CompletedTask;
    }

    public SequencedMachineObservation[] ReadObservations(
        ObservationStreamId streamId)
    {
        ArgumentNullException.ThrowIfNull(streamId);

        lock (_gate)
        {
            return _observations
                .Where(pair => pair.Key.StreamId == streamId)
                .OrderBy(pair => pair.Key.InstanceId)
                .ThenBy(pair => pair.Key.Sequence)
                .Select(pair => pair.Value)
                .ToArray();
        }
    }

    private Dictionary<ObservationKey, SequencedMachineObservation>
        StageObservations(ObservationIngestionBatch batch)
    {
        Dictionary<ObservationKey, SequencedMachineObservation> pending = [];
        var isIdempotentReplay =
            _checkpoints.TryGetValue(
                batch.Checkpoint.StreamId,
                out var current) &&
            current == batch.Checkpoint;

        foreach (var item in batch.Observations)
        {
            var key = new ObservationKey(
                batch.Checkpoint.StreamId,
                batch.Checkpoint.InstanceId,
                item.Sequence);

            if ((_observations.TryGetValue(key, out var existing) &&
                 existing != item) ||
                (pending.TryGetValue(key, out var staged) &&
                 staged != item))
            {
                throw new InvalidOperationException(
                    "The stream already contains a different observation " +
                    "at the same instance and sequence.");
            }

            if (isIdempotentReplay &&
                !_observations.ContainsKey(key))
            {
                throw new InvalidOperationException(
                    "An idempotent replay cannot add observations to an " +
                    "already committed checkpoint.");
            }

            pending.TryAdd(key, item);
        }

        return pending;
    }

    private void ValidateBatch(ObservationIngestionBatch batch)
    {
        _checkpoints.TryGetValue(
            batch.Checkpoint.StreamId,
            out var current);

        var isIdempotentReplay = current == batch.Checkpoint;

        if (!isIdempotentReplay &&
            current != batch.ExpectedCheckpoint)
        {
            throw new InvalidOperationException(
                "The durable checkpoint no longer matches the expected state.");
        }

        if (!isIdempotentReplay &&
            current is not null &&
            current.InstanceId == batch.Checkpoint.InstanceId &&
            batch.Checkpoint.NextSequence < current.NextSequence)
        {
            throw new InvalidOperationException(
                "A checkpoint cannot move backwards within an Agent instance.");
        }

        foreach (var item in batch.Observations)
        {
            if (item.Observation.MachineId !=
                batch.Checkpoint.StreamId.MachineId)
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

    private readonly record struct ObservationKey(
        ObservationStreamId StreamId,
        ulong InstanceId,
        ulong Sequence);
}
