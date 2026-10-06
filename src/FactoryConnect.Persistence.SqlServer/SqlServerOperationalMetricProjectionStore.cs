using Microsoft.Data.SqlClient;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed class SqlServerOperationalMetricProjectionStore : IOperationalMetricProjectionStore
{
    private readonly string _connectionString;
    private readonly SqlServerOperationalMetricProjectionCommitTransaction _commitTransaction;
    private readonly SqlServerOperationalMetricProjectionQueryReader _queryReader;
    private readonly SqlServerOperationalMetricProjectionSummaryReader _summaryReader;

    public SqlServerOperationalMetricProjectionStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
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

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return await SqlServerOperationalMetricProjectionStableRead.ExecuteAsync<OperationalMetricProjectionCheckpoint?>(
            connection, processorId, async (c, transaction, token) =>
            {
                var header = await SqlServerOperationalMetricProjectionCommitTransaction.ReadCheckpointHeaderAsync(
                    c, transaction, processorId, token);
                if (header is null) return null;
                if (header.StreamId != sourceStreamId)
                    throw new InvalidOperationException("Projection checkpoint belongs to a different metric input stream.");
                var summaries = await SqlServerOperationalMetricProjectionSummaryReader.ReadSummariesAsync(
                    c, transaction, processorId, true, token);
                return new OperationalMetricProjectionCheckpoint(processorId,
                    new MetricAggregationCheckpoint(header.AggregationProcessorId, header.StreamId, header.Position),
                    new OperationalMetricProjectionBatchManifest(summaries.Select(summary => summary.Key)));
            }, cancellationToken);
    }

    public ValueTask<OperationalMetricProjection?> ReadProjectionAsync(
        OperationalMetricProjectionProcessorId processorId,
        OperationalMetricEvaluationKey key,
        CancellationToken cancellationToken) =>
        _queryReader.ReadDetailAsync(processorId, key, cancellationToken);

    public ValueTask CommitAsync(
        OperationalMetricProjectionCommit commit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        return new ValueTask(_commitTransaction.ExecuteAsync(
            commit,
            async (context, token) =>
                await SqlServerOperationalMetricProjectionPublication.ExecuteAsync(
                    context,
                    commit,
                    token),
            cancellationToken));
    }
}
