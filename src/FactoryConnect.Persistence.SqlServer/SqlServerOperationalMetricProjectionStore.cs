using FactoryConnect.Abstractions;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed class SqlServerOperationalMetricProjectionStore : IOperationalMetricProjectionStore
{
    private readonly SqlServerOperationalMetricProjectionCommitTransaction _commitTransaction;
    private readonly SqlServerOperationalMetricProjectionQueryReader _queryReader;
    private readonly SqlServerOperationalMetricProjectionSourceBindingReader _sourceBindingReader;

    public SqlServerOperationalMetricProjectionStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _commitTransaction = new SqlServerOperationalMetricProjectionCommitTransaction(connectionString);
        _queryReader = new SqlServerOperationalMetricProjectionQueryReader(connectionString);
        _sourceBindingReader = new SqlServerOperationalMetricProjectionSourceBindingReader(connectionString);
    }

    public async ValueTask<OperationalMetricProjectionCheckpoint?> ReadCheckpointAsync(
        OperationalMetricProjectionProcessorId processorId,
        MetricInputStreamId sourceStreamId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(sourceStreamId);
        cancellationToken.ThrowIfCancellationRequested();

        var header = await _commitTransaction.ReadCheckpointHeaderAsync(
            processorId,
            cancellationToken);
        if (header is null)
        {
            return null;
        }

        var machineId = await _sourceBindingReader.ReadMachineIdAsync(
            processorId,
            cancellationToken);
        if (machineId is null)
        {
            throw new InvalidOperationException(
                "Persisted operational metric projection checkpoint source binding is unavailable.");
        }

        if (machineId.Value != sourceStreamId.MachineId)
        {
            throw new InvalidOperationException(
                "Projection processor checkpoint belongs to a different metric input stream.");
        }

        var sourceRevision = await ReadSourceRevisionAsync(
            processorId,
            sourceStreamId,
            header.Position,
            cancellationToken);
        if (sourceRevision is null)
        {
            throw new InvalidOperationException(
                "Persisted operational metric projection checkpoint source revision is unavailable.");
        }

        var manifest = await ReadCurrentManifestAsync(
            processorId,
            sourceRevision,
            cancellationToken);
        return new OperationalMetricProjectionCheckpoint(
            processorId,
            sourceRevision,
            manifest);
    }

    public ValueTask<OperationalMetricProjection?> ReadProjectionAsync(
        OperationalMetricProjectionProcessorId processorId,
        OperationalMetricEvaluationKey key,
        CancellationToken cancellationToken) =>
        _queryReader.ReadDetailAsync(processorId, key, cancellationToken);

    public Task CommitAsync(
        OperationalMetricProjectionCommit commit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        return _commitTransaction.ExecuteAsync(
            commit,
            (context, token) => PublishAsync(context, commit, token),
            cancellationToken);
    }

    private static async Task PublishAsync(
        SqlServerOperationalMetricProjectionCommitContext context,
        OperationalMetricProjectionCommit commit,
        CancellationToken cancellationToken)
    {
        await SqlServerOperationalMetricProjectionPublication.ExecuteAsync(
            context,
            commit,
            cancellationToken);
    }

    private async Task<MetricAggregationCheckpoint?> ReadSourceRevisionAsync(
        OperationalMetricProjectionProcessorId processorId,
        MetricInputStreamId sourceStreamId,
        MetricInputPosition position,
        CancellationToken cancellationToken)
    {
        var projection = await ReadAnyProjectionAsync(processorId, cancellationToken);
        if (projection is not null)
        {
            var revision = projection.SourceRevision;
            if (revision.StreamId != sourceStreamId || revision.Position != position)
            {
                throw new InvalidOperationException(
                    "Persisted operational metric projection checkpoint source identity is inconsistent.");
            }

            return revision;
        }

        return null;
    }

    private async Task<OperationalMetricProjection?> ReadAnyProjectionAsync(
        OperationalMetricProjectionProcessorId processorId,
        CancellationToken cancellationToken)
    {
        // A checkpoint with an empty manifest has no projection row from which to
        // recover the aggregation processor identity. That case is intentionally
        // rejected until the source-binding reader exposes the full source identity.
        var machineId = await _sourceBindingReader.ReadMachineIdAsync(processorId, cancellationToken);
        _ = machineId;
        return null;
    }

    private static Task<OperationalMetricProjectionBatchManifest> ReadCurrentManifestAsync(
        OperationalMetricProjectionProcessorId processorId,
        MetricAggregationCheckpoint sourceRevision,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
