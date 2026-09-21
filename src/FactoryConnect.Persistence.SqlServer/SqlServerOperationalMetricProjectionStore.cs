using FactoryConnect.Abstractions;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed class SqlServerOperationalMetricProjectionStore : IOperationalMetricProjectionStore
{
    private readonly SqlServerOperationalMetricProjectionCommitTransaction _commitTransaction;
    private readonly SqlServerOperationalMetricProjectionQueryReader _queryReader;
    private readonly SqlServerOperationalMetricProjectionSummaryReader _summaryReader;

    public SqlServerOperationalMetricProjectionStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _commitTransaction = new SqlServerOperationalMetricProjectionCommitTransaction(connectionString);
        _queryReader = new SqlServerOperationalMetricProjectionQueryReader(connectionString);
        _summaryReader = new SqlServerOperationalMetricProjectionSummaryReader(connectionString);
    }

    public async ValueTask<OperationalMetricProjectionCheckpoint?> ReadCheckpointAsync(
        OperationalMetricProjectionProcessorId processorId,
        MetricInputStreamId sourceStreamId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(sourceStreamId);
        cancellationToken.ThrowIfCancellationRequested();

        var header = await _commitTransaction.ReadCheckpointHeaderAsync(processorId, cancellationToken);
        if (header is null)
        {
            return null;
        }

        if (header.StreamId != sourceStreamId)
        {
            throw new InvalidOperationException(
                "Projection processor checkpoint belongs to a different metric input stream.");
        }

        var manifest = await _summaryReader.ReadCurrentManifestAsync(processorId, cancellationToken);
        return new OperationalMetricProjectionCheckpoint(
            processorId,
            new MetricAggregationCheckpoint(
                header.AggregationProcessorId,
                header.StreamId,
                header.Position),
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
            async (context, token) =>
                await SqlServerOperationalMetricProjectionPublication.ExecuteAsync(
                    context,
                    commit,
                    token),
            cancellationToken);
    }
}
