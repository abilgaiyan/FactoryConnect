namespace FactoryConnect.Abstractions;

/// <summary>
/// Processes ordered batches of durable machine observations.
/// </summary>
/// <remarks>
/// Delivery is at least once. A batch may be delivered again when processing
/// succeeds but its processing checkpoint cannot be committed. Implementations
/// must therefore be idempotent by <see cref="ObservationPosition"/>, or commit
/// derived effects and processing progress atomically where appropriate.
/// </remarks>
public interface IObservationProcessor
{
    /// <summary>
    /// Gets the stable identity whose progress is tracked independently.
    /// </summary>
    ObservationProcessorId ProcessorId { get; }

    /// <summary>
    /// Processes one ordered durable observation batch.
    /// </summary>
    /// <param name="observations">
    /// Observations ordered by strictly increasing durable position.
    /// </param>
    /// <param name="cancellationToken">
    /// Token used to cancel processing.
    /// </param>
    /// <returns>A value task representing processing completion.</returns>
    ValueTask ProcessAsync(
        IReadOnlyList<DurableMachineObservation> observations,
        CancellationToken cancellationToken = default);
}
