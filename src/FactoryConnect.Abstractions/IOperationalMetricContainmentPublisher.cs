namespace FactoryConnect.Abstractions;

/// <summary>Explicit format-2 publication boundary; never routes through the guarded V1 commit contract.</summary>
public interface IOperationalMetricContainmentPublisher
{
    ValueTask PublishAsync(OperationalMetricProjectionCheckpoint? expectedCheckpoint,
        OperationalMetricProjectionCheckpoint proposedCheckpoint, IReadOnlyList<OperationalMetricProjection> projections,
        CancellationToken cancellationToken);
}
