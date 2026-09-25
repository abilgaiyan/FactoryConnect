using FactoryConnect.Abstractions;

namespace FactoryConnect.Edge;

/// <summary>Explicit demo-only normalization of the fixture's MTConnect Execution event.</summary>
public sealed class DemoExecutionMappingProcessor(IObservationProcessor inner) : IObservationProcessor
{
    public ObservationProcessorId ProcessorId => inner.ProcessorId;

    public ValueTask ProcessAsync(
        IReadOnlyList<DurableMachineObservation> observations,
        CancellationToken cancellationToken = default) =>
        inner.ProcessAsync(observations.Select(Normalize).ToArray(), cancellationToken);

    private static DurableMachineObservation Normalize(DurableMachineObservation item)
    {
        var observation = item.Observation;
        if (!string.Equals(observation.Source, "mtconnect", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(observation.Address, "exec", StringComparison.OrdinalIgnoreCase) ||
            observation.Type != SignalType.Enumeration)
        {
            return item;
        }

        bool? running = observation.Quality != ObservationQuality.Good
            ? null
            : observation.Value switch
        {
            "ACTIVE" => true,
            "READY" => false,
            _ => throw new InvalidDataException("Demo Execution must be ACTIVE or READY."),
        };

        return new DurableMachineObservation(
            item.Position, item.StreamId, item.InstanceId, item.Sequence,
            observation with { Type = SignalType.Digital, Value = running });
    }
}
