using System.Data;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlServerOperationalMetricProjectionStableRead
{
    internal const int MaximumAttempts = 3;

    public static async Task<T> ExecuteAsync<T>(
        SqlConnection connection,
        long projectionProcessorRowId,
        Func<SqlConnection, CancellationToken, Task<T>> readPublication,
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

            var result = await readPublication(connection, cancellationToken);

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

    private static async Task<decimal?> ReadCheckpointPositionAsync(
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

        return Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
