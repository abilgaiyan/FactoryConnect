namespace FactoryConnect.Abstractions;

public interface IObservationProcessor
{
    ObservationProcessorId ProcessorId { get; }

    ValueTask ProcessAsync(
        IReadOnlyList<DurableMachineObservation> observations,
        CancellationToken cancellationToken = default);
}
