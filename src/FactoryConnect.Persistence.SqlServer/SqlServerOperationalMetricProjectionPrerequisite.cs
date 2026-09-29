using FactoryConnect.Abstractions;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>
/// Converges the selected aggregation revision before the operational runtime
/// can request a batch or commit its first projection at that revision.
/// </summary>
internal sealed class SqlServerOperationalMetricProjectionPrerequisite(
    string connectionString) : IOperationalMetricProjectionPrerequisite
{
    private readonly SqlServerMetricAggregationStore _aggregationStore = new(connectionString);
    private readonly SqlServerProductionReferenceTimeConvergenceCoordinator _coordinator = new(connectionString);
    private readonly SqlServerProductionReferenceTimePublicationTransitionStore _transitions = new(connectionString);

    public async ValueTask<MetricAggregationCheckpoint?> PrepareAsync(
        OperationalMetricEvaluationBatchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var next = await _aggregationStore.ReadNextAsync(
            request.SourceProcessorId,
            request.SourceStreamId,
            request.KnownRevision,
            cancellationToken);
        if (next is not null)
        {
            var revision = next.Revision;
            if (revision.ProcessorId != request.SourceProcessorId ||
                revision.StreamId != request.SourceStreamId)
            {
                throw new InvalidDataException("The selected aggregation revision belongs to a different processor or stream.");
            }

            await _coordinator.ConvergeAsync(
                revision,
                request.SourceStreamId.MachineId,
                request.KnownRevision?.Position,
                cancellationToken);
            var completion = await _transitions.ReadAsync(
                revision.ProcessorId,
                revision.Position,
                cancellationToken);
            return completion is { IsCompleted: true, CompletedReferenceTimeRevision: not null }
                ? revision
                : null;
        }

        if (request.KnownRevision is null)
        {
            return null;
        }

        var exact = await _aggregationStore.ReadExactAsync(request.KnownRevision, cancellationToken);
        if (exact is null)
        {
            throw new InvalidOperationException(
                "The durable operational projection checkpoint has no exact FC-026 aggregation revision.");
        }

        var replayCompletion = await _transitions.ReadAsync(
            exact.Revision.ProcessorId,
            exact.Revision.Position,
            cancellationToken);
        if (replayCompletion is not { IsCompleted: true, CompletedReferenceTimeRevision: not null })
        {
            throw new InvalidOperationException(
                "An existing operational projection checkpoint has no completed reference-time transition at its exact aggregation revision.");
        }

        return exact.Revision;
    }
}
