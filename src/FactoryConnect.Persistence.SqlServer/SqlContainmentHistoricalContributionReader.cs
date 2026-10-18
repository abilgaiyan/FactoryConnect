using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>
/// P3 source-membership reader. This is a historical input, not a completed
/// authority validator: aggregation arithmetic and R/S bindings are separate.
/// The caller owns the open connection and active transaction.
/// </summary>
internal static class SqlContainmentHistoricalContributionReader
{
    internal sealed record Row(
        long FactRowId,
        decimal Position,
        string FactId,
        string Key,
        string Value,
        string Unit,
        DateTimeOffset StartsAtUtc,
        DateTimeOffset EndsAtUtc,
        string OccurrenceSiteId,
        string OccurrenceShiftId,
        string OccurrenceAssignmentId,
        DateTimeOffset OccurrenceStartsAtUtc,
        DateTimeOffset OccurrenceEndsAtUtc,
        string ProductionDaySiteId,
        DateTime ProductionBusinessDate,
        bool? IsPlannedProductionTime,
        string? EligibilityIntervalId,
        string? ContextualizedActivityIntervalId);

    /// <summary>
    /// Reads only contributions incorporated by the selected aggregation
    /// processor at the exact cut. Does not substitute the raw fact stream,
    /// which may contain facts never admitted to this processor.
    /// </summary>
    internal static async Task<IReadOnlyList<Row>> ReadAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long aggregationProcessorRowId,
        long inputStreamRowId,
        decimal revisionPosition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (connection.State != ConnectionState.Open ||
            !ReferenceEquals(transaction.Connection, connection))
            throw new InvalidOperationException("Historical authority requires the caller's open transaction.");
        if (aggregationProcessorRowId <= 0 || inputStreamRowId <= 0 ||
            revisionPosition < 1 || revisionPosition > 18446744073709551615m ||
            decimal.Truncate(revisionPosition) != revisionPosition)
            throw new ArgumentOutOfRangeException(nameof(revisionPosition), "Invalid historical source cut.");

        // Verify the exact processor/stream/revision before reading its members.
        await using (var exact = connection.CreateCommand())
        {
            exact.Transaction = transaction;
            exact.CommandText = """
                SELECT COUNT_BIG(*)
                FROM dbo.MetricAggregationRevision AS r
                INNER JOIN dbo.MetricAggregationProcessor AS p
                  ON p.MetricAggregationProcessorRowId = r.MetricAggregationProcessorRowId
                WHERE r.MetricAggregationProcessorRowId = @processor
                  AND p.MetricInputStreamRowId = @stream
                  AND r.Position = @cut;
                """;
            Bind(exact, aggregationProcessorRowId, inputStreamRowId, revisionPosition);
            if (Convert.ToInt64(await exact.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 1)
                throw new InvalidDataException("The exact aggregation source cut is absent or has a different stream.");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT f.MetricInputFactRowId, f.Position, f.FactId,
                   f.MetricInputKey, f.MetricValue, f.Unit,
                   f.StartsAtUtc, f.EndsAtUtc,
                   f.OccurrenceSiteId, f.OccurrenceShiftId,
                   f.OccurrenceShiftScheduleAssignmentId,
                   f.OccurrenceStartsAtUtc, f.OccurrenceEndsAtUtc,
                   f.ProductionDaySiteId, f.ProductionBusinessDate,
                   f.IsPlannedProductionTime,
                   f.SourceEligibilityIntervalId,
                   f.SourceContextualizedActivityIntervalId
            FROM dbo.MetricAggregationContribution AS c
            INNER JOIN dbo.MetricInputFact AS f
              ON f.MetricInputFactRowId = c.MetricInputFactRowId
             AND f.MetricInputStreamRowId = c.MetricInputStreamRowId
             AND f.Position = c.Position
            WHERE c.MetricAggregationProcessorRowId = @processor
              AND c.MetricInputStreamRowId = @stream
              AND c.Position <= @cut
            ORDER BY c.Position, c.MetricInputFactRowId;
            """;
        Bind(command, aggregationProcessorRowId, inputStreamRowId, revisionPosition);
        var rows = new List<Row>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        decimal previous = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            var position = reader.GetDecimal(1);
            if (position <= previous)
                throw new InvalidDataException("Historical contribution positions are not strictly increasing.");
            previous = position;
            rows.Add(new Row(
                reader.GetInt64(0), position, reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetFieldValue<DateTimeOffset>(6), reader.GetFieldValue<DateTimeOffset>(7),
                reader.GetString(8), reader.GetString(9), reader.GetString(10),
                reader.GetFieldValue<DateTimeOffset>(11), reader.GetFieldValue<DateTimeOffset>(12),
                reader.GetString(13), reader.GetDateTime(14),
                reader.IsDBNull(15) ? null : reader.GetBoolean(15),
                reader.IsDBNull(16) ? null : reader.GetString(16),
                reader.IsDBNull(17) ? null : reader.GetString(17)));
        }
        return rows;
    }

    private static void Bind(SqlCommand command, long processor, long stream, decimal cut)
    {
        command.Parameters.Add("@processor", SqlDbType.BigInt).Value = processor;
        command.Parameters.Add("@stream", SqlDbType.BigInt).Value = stream;
        var parameter = command.Parameters.Add("@cut", SqlDbType.Decimal);
        parameter.Precision = 20;
        parameter.Scale = 0;
        parameter.Value = cut;
    }
}
