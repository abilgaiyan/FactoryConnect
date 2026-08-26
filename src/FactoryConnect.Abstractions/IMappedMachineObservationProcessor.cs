namespace FactoryConnect.Abstractions;

/// <summary>
/// Processes ordered batches of durable canonical machine observations.
/// </summary>
/// <remarks>
/// Delivery is at least once. Implementations must tolerate equivalent replay
/// by durable observation position.
/// </remarks>
public interface IMappedMachineObservationProcessor
{
    ObservationProcessorId ProcessorId { get; }

    ValueTask ProcessAsync(
        IReadOnlyList<DurableMappedMachineObservation> observations,
        CancellationToken cancellationToken = default);
}
