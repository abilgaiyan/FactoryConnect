using System.Data;
using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlServerOperationalMetricProjectionStableRead
{
    internal const int MaximumAttempts = 3;

    public static Task<T> ExecuteAsync<T>(
        SqlConnection connection,
        long projectionProcessorRowId,
        Func<SqlConnection, CancellationToken, Task<T>> readPublication,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readPublication);

        return ExecuteAsync(
            connection,
            projectionProcessorRowId,
            (stableConnection, _, token) => readPublication(stableConnection, token),
            cancellationToken);
    }

    public static async Task<T> ExecuteAsync<T>(
        SqlConnection connection,
        long projectionProcessorRowId,
        Func<SqlConnection, MetricInputPosition?, CancellationToken, Task<T>> readPublication,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(readPublication);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(projectionProcessorRowId);

        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException(
                "Operational metric projection stable reads require an open SQL connection.");
        }

        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var checkpointBefore = await ReadCheckpointPositionAsync(
                connection,
                projectionProcessorRowId,
                cancellationToken);

            await ValidateManifestRevisionCoherenceAsync(
                connection,
                projectionProcessorRowId,
                checkpointBefore,
                cancellationToken);

            var result = await readPublication(connection, checkpointBefore, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            var checkpointAfter = await ReadCheckpointPositionAsync(
                connection,
                projectionProcessorRowId,
                cancellationToken);

            if (checkpointBefore == checkpointAfter)
            {
                return result;
            }
        }

        throw new InvalidOperationException(
            "Operational metric projection publication did not remain checkpoint-stable within the configured read retry budget.");
    }

    private static async Task<MetricInputPosition?> ReadCheckpointPositionAsync(
        SqlConnection connection,
        long projectionProcessorRowId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
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

    private static async Task ValidateManifestRevisionCoherenceAsync(
        SqlConnection connection,
        long projectionProcessorRowId,
        MetricInputPosition? checkpointPosition,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = projectionProcessorRowId;

        if (checkpointPosition is null)
        {
            command.CommandText =
                "SELECT COUNT_BIG(*) " +
                "FROM dbo.OperationalMetricProjectionManifest " +
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
                "AND p.SourceRevisionPosition <> @CheckpointPosition;";
            command.Parameters.Add(
                SqlServerUInt64.CreateParameter(
                    "@CheckpointPosition",
                    checkpointPosition.Value));
        }

        var mismatchCount = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
        if (mismatchCount != 0)
        {
            throw new InvalidOperationException(
                "Persisted operational metric projection source revision is corrupt or inconsistent with the current publication checkpoint.");
        }
    }
}
