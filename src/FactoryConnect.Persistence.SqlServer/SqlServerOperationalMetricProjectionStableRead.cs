using System.Data;
using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlServerOperationalMetricProjectionStableRead
{
    internal const int MaximumAttempts = 3;

    public static Task<T> ExecuteAsync<T>(SqlConnection connection, long projectionProcessorRowId,
        Func<SqlConnection, CancellationToken, Task<T>> readPublication, CancellationToken cancellationToken) =>
        ExecuteAsync(connection, projectionProcessorRowId,
            (c, _, token) => readPublication(c, token), cancellationToken);

    public static async Task<T> ExecuteAsync<T>(SqlConnection connection, long projectionProcessorRowId,
        Func<SqlConnection, MetricInputPosition?, CancellationToken, Task<T>> readPublication,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readPublication);
        // Legacy internal test seam resolves only immutable processor identity before entering the gate.
        await using var identity = connection.CreateCommand();
        identity.CommandText = "SELECT ProcessorKey FROM dbo.OperationalMetricProjectionProcessor WHERE OperationalMetricProjectionProcessorRowId=@Id";
        identity.Parameters.Add("@Id", SqlDbType.BigInt).Value = projectionProcessorRowId;
        var key = await identity.ExecuteScalarAsync(cancellationToken) as string
            ?? throw new InvalidOperationException("Projection processor identity is unavailable.");
        return await ExecuteAsync(connection, new OperationalMetricProjectionProcessorId(key),
            async (c, transaction, token) =>
            {
                for (var attempt = 0; attempt < MaximumAttempts; attempt++)
                {
                    var before = await ReadCheckpointPositionAsync(c, transaction, projectionProcessorRowId, token);
                    var coherent = await IsManifestRevisionCoherentAsync(c, transaction, projectionProcessorRowId, before, token);
                    var result = await readPublication(c, before, token);
                    var after = await ReadCheckpointPositionAsync(c, transaction, projectionProcessorRowId, token);
                    if (before != after) continue;
                    if (!coherent) throw new InvalidOperationException("Persisted projection source revision is corrupt.");
                    return result;
                }
                throw new InvalidOperationException("Projection read exceeded the checkpoint retry budget.");
            }, cancellationToken, observeCheckpointChanges: false);
    }

    public static async Task<T> ExecuteAsync<T>(SqlConnection connection,
        OperationalMetricProjectionProcessorId processorId,
        Func<SqlConnection, SqlTransaction, CancellationToken, Task<T>> readPublication,
        CancellationToken cancellationToken, bool observeCheckpointChanges = true)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(readPublication);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            await SqlServerOperationalMetricProjectionProcessorGate.AcquireAsync(connection, transaction,
                processorId, OperationalMetricProjectionProcessorGateMode.Shared, cancellationToken);
            for (var attempt = 0; attempt < MaximumAttempts; attempt++)
            {
                var before = observeCheckpointChanges
                    ? await ReadProcessorPositionAsync(connection, transaction, processorId, cancellationToken) : null;
                T result;
                try { result = await readPublication(connection, transaction, cancellationToken); }
                catch (InvalidOperationException) when (observeCheckpointChanges)
                {
                    // Defensive only: a non-participating external writer may change checkpoint while a read waits.
                    var afterFailure = await ReadProcessorPositionAsync(connection, transaction, processorId, cancellationToken);
                    if (before != afterFailure) continue;
                    throw;
                }
                var after = observeCheckpointChanges
                    ? await ReadProcessorPositionAsync(connection, transaction, processorId, cancellationToken) : null;
                if (before != after) continue;
                cancellationToken.ThrowIfCancellationRequested();
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            throw new InvalidOperationException("Projection read exceeded the checkpoint retry budget.");
        }
        catch (SqlException exception) when (cancellationToken.IsCancellationRequested)
        {
            try { await transaction.RollbackAsync(CancellationToken.None); } catch { /* Preserve cancellation. */ }
            throw new OperationCanceledException("Operational metric projection read was cancelled.", exception, cancellationToken);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); } catch { /* Preserve primary failure. */ }
            throw;
        }
    }

    private static async Task<MetricInputPosition?> ReadProcessorPositionAsync(SqlConnection connection,
        SqlTransaction transaction, OperationalMetricProjectionProcessorId processorId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT c.Position FROM dbo.OperationalMetricProjectionProcessor p " +
            "LEFT JOIN dbo.OperationalMetricProjectionCheckpoint c ON c.OperationalMetricProjectionProcessorRowId=p.OperationalMetricProjectionProcessorRowId " +
            "WHERE p.ProcessorKeyBinary=@Key";
        command.Parameters.Add("@Key", SqlDbType.VarBinary, StringOrderKeyV2Codec.MaximumEncodedLength).Value =
            StringOrderKeyV2Codec.Encode(processorId.Value);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : new MetricInputPosition(SqlServerUInt64.Materialize(
            Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture)));
    }

    internal static async Task ValidateRevisionCoherenceAsync(SqlConnection connection, SqlTransaction transaction,
        long processorRowId, CancellationToken cancellationToken)
    {
        var position = await ReadCheckpointPositionAsync(connection, transaction, processorRowId, cancellationToken);
        if (!await IsManifestRevisionCoherentAsync(connection, transaction, processorRowId, position, cancellationToken))
            throw new InvalidOperationException("Persisted operational metric projection source revision is corrupt or inconsistent with the publication checkpoint.");
    }

    private static async Task<MetricInputPosition?> ReadCheckpointPositionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long projectionProcessorRowId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT Position " +
            "FROM dbo.OperationalMetricProjectionCheckpoint " +
            "WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId;";
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = projectionProcessorRowId;

        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null || value is DBNull)
        {
            return null;
        }

        var materialized = SqlServerUInt64.Materialize(
            Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture));
        return new MetricInputPosition(materialized);
    }

    private static async Task<bool> IsManifestRevisionCoherentAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long projectionProcessorRowId,
        MetricInputPosition? checkpointPosition,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = projectionProcessorRowId;

        if (checkpointPosition is null)
        {
            command.CommandText =
                "SELECT COUNT_BIG(*) " +
                "FROM dbo.OperationalMetricProjection " +
                "WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId;";
        }
        else
        {
            command.CommandText =
                "SELECT COUNT_BIG(*) " +
                "FROM dbo.OperationalMetricProjectionManifest AS m " +
                "INNER JOIN dbo.OperationalMetricProjection AS p " +
                "ON p.OperationalMetricProjectionProcessorRowId = m.OperationalMetricProjectionProcessorRowId " +
                "AND p.OperationalMetricProjectionRowId = m.OperationalMetricProjectionRowId " +
                "WHERE m.OperationalMetricProjectionProcessorRowId = @ProcessorRowId " +
                "AND p.SourceRevisionPosition <> @CheckpointPosition " +
                "; SELECT COUNT_BIG(*) FROM dbo.OperationalMetricProjection AS p " +
                "WHERE p.OperationalMetricProjectionProcessorRowId = @ProcessorRowId " +
                "AND p.SourceRevisionPosition > @CheckpointPosition;";
            command.Parameters.Add(
                SqlServerUInt64.CreateParameter(
                    "@CheckpointPosition",
                    checkpointPosition.Value));
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        do
        {
            if (await reader.ReadAsync(cancellationToken) && reader.GetInt64(0) != 0)
            {
                return false;
            }
        }
        while (await reader.NextResultAsync(cancellationToken));
        return true;
    }
}
