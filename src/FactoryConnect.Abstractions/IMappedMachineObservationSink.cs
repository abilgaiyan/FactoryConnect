namespace FactoryConnect.Abstractions;

/// <summary>
/// Stores canonical mapped observations produced from durable observations.
/// </summary>
/// <remarks>
/// Delivery is at least once. Writing an equivalent observation again at the
/// same stream and position must be idempotent. A different observation at an
/// already-written stream and position must be rejected.
/// </remarks>
public interface IMappedMachineObservationSink
{
    ValueTask WriteAsync(
        IReadOnlyList<DurableMappedMachineObservation> observations,
        CancellationToken cancellationToken = default);
}
