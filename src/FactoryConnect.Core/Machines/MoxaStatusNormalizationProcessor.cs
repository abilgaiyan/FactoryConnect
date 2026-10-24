using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Machines;

/// <summary>Normalizes the commissioning adapter status into one atomic canonical state enumeration.
/// Durable raw observations are never mutated. Unknown values remove state evidence.</summary>
public sealed class MoxaStatusNormalizationProcessor : IObservationProcessor
{
    private readonly IObservationProcessor _inner;
    private readonly ObservationStreamId _streamId;
    private readonly string _source;
    private readonly string _address;

    public MoxaStatusNormalizationProcessor(
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
            throw new InvalidOperationException("MOXA status normalization requires its configured observation stream.");
        }

        var observation = item.Observation;
        if (!string.Equals(observation.Source, _source, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(observation.Address, _address, StringComparison.OrdinalIgnoreCase))
        {
            return item;
        }

        if (observation.Type != SignalType.Enumeration)
        {
            throw new InvalidDataException("Configured MOXA status input must be an Enumeration observation.");
        }

        string? state = observation.Quality != ObservationQuality.Good
            ? null
            : observation.Value switch
            {
                "RUNNING" => "RUNNING",
                "IDLE" => "IDLE",
                "ALARM" => "FAULT",
                // Gajra's all-zero rule is operational, not proof of electrical power state.
                "OFF" => "STOPPED",
                _ => null,
            };
        var quality = observation.Quality == ObservationQuality.Good && state is null
            ? ObservationQuality.Uncertain
            : observation.Quality;

        return new DurableMachineObservation(
            item.Position, item.StreamId, item.InstanceId, item.Sequence,
            observation with { Type = SignalType.Enumeration, Value = state, Quality = quality });
    }
}
