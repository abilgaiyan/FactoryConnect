using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Machines;

/// <summary>Normalizes a configured execution enumeration for the running Boolean projection.
/// Durable raw observations are never mutated. Unknown values remove running evidence.</summary>
public sealed class ExecutionNormalizationProcessor : IObservationProcessor
{
    private readonly IObservationProcessor _inner;
    private readonly ObservationStreamId _streamId;
    private readonly string _source;
    private readonly string _address;

    public ExecutionNormalizationProcessor(
        IObservationProcessor inner,
        ObservationStreamId streamId,
        string source,
        string address)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(streamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        _inner = inner;
        _streamId = streamId;
        _source = source;
        _address = address;
    }

    public ObservationProcessorId ProcessorId => _inner.ProcessorId;

    public ValueTask ProcessAsync(
        IReadOnlyList<DurableMachineObservation> observations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observations);
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = new DurableMachineObservation[observations.Count];
        for (var index = 0; index < observations.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            normalized[index] = Normalize(observations[index]);
        }

        return _inner.ProcessAsync(normalized, cancellationToken);
    }

    private DurableMachineObservation Normalize(DurableMachineObservation item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.StreamId != _streamId)
        {
            throw new InvalidOperationException("Execution normalization requires its configured observation stream.");
        }

        var observation = item.Observation;
        if (!string.Equals(observation.Source, _source, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(observation.Address, _address, StringComparison.OrdinalIgnoreCase))
        {
            return item;
        }

        if (observation.Type != SignalType.Enumeration)
        {
            throw new InvalidDataException("Configured execution input must be an Enumeration observation.");
        }

        bool? running = observation.Quality != ObservationQuality.Good
            ? null
            : observation.Value switch
            {
                "ACTIVE" => true,
                "READY" or "FEED_HOLD" or "OPTIONAL_STOP" or "PROGRAM_STOPPED" or "STOPPED" => false,
                _ => null,
            };
        var quality = observation.Quality == ObservationQuality.Good && running is null
            ? ObservationQuality.Uncertain
            : observation.Quality;

        return new DurableMachineObservation(
            item.Position, item.StreamId, item.InstanceId, item.Sequence,
            observation with { Type = SignalType.Digital, Value = running, Quality = quality });
    }
}
