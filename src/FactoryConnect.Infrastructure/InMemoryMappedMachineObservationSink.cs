using FactoryConnect.Abstractions;

namespace FactoryConnect.Infrastructure;

public sealed class InMemoryMappedMachineObservationSink :
    IMappedMachineObservationSink
{
    private readonly Lock _gate = new();
    private readonly Dictionary<
        MappedObservationKey,
        DurableMappedMachineObservation> _observations = [];

    public ValueTask WriteAsync(
        IReadOnlyList<DurableMappedMachineObservation> observations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observations);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            Dictionary<
                MappedObservationKey,
                DurableMappedMachineObservation> pending = [];

            foreach (var observation in observations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(observation);

                var key = new MappedObservationKey(
                    observation.StreamId,
                    observation.Position);

                if ((_observations.TryGetValue(key, out var existing) &&
                     existing != observation) ||
                    (pending.TryGetValue(key, out var staged) &&
                     staged != observation))
                {
                    throw new InvalidOperationException(
                        "The mapped observation stream already contains a " +
                        "different observation at the same durable position.");
                }

                pending.TryAdd(key, observation);
            }

            foreach (var pair in pending)
            {
                _observations.TryAdd(pair.Key, pair.Value);
            }
        }

        return ValueTask.CompletedTask;
    }

    public DurableMappedMachineObservation[] ReadObservations(
        ObservationStreamId streamId)
    {
        ArgumentNullException.ThrowIfNull(streamId);

        lock (_gate)
        {
            return _observations.Values
                .Where(observation => observation.StreamId == streamId)
                .OrderBy(observation => observation.Position)
                .ToArray();
        }
    }

    private readonly record struct MappedObservationKey(
        ObservationStreamId StreamId,
        ObservationPosition Position);
}
