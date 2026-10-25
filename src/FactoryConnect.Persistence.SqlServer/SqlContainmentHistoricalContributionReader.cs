using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>Reads complete incorporated facts from the caller's stable historical transaction.</summary>
internal static class SqlContainmentHistoricalContributionReader
{
    internal sealed record ValidatedHistoricalContributions(
        IReadOnlyList<PositionedMetricInputFact> Inputs,
        MetricAggregateContributionSet Aggregates);
    internal static void RequireStableTransaction(SqlConnection connection, SqlTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (connection.State != ConnectionState.Open || !ReferenceEquals(transaction.Connection, connection) ||
            transaction.IsolationLevel != IsolationLevel.Serializable)
            throw new InvalidOperationException("Historical authority requires the caller's open Serializable transaction.");
    }

    internal static async Task<IReadOnlyList<PositionedMetricInputFact>> ReadAsync(
        SqlConnection connection, SqlTransaction transaction, MetricAggregationCheckpoint cut,
        CancellationToken cancellationToken) =>
        (await ReadValidatedAsync(connection, transaction, cut, cancellationToken)).Inputs;

    internal static async Task<ValidatedHistoricalContributions> ReadValidatedAsync(
        SqlConnection connection, SqlTransaction transaction, MetricAggregationCheckpoint cut,
        CancellationToken cancellationToken)
    {
        RequireStableTransaction(connection, transaction);
        ArgumentNullException.ThrowIfNull(cut);
        cancellationToken.ThrowIfCancellationRequested();
        long processorRowId;
        long streamRowId;
        await using (var exact = connection.CreateCommand())
        {
            exact.Transaction = transaction;
            exact.CommandText = """
                SELECT p.MetricAggregationProcessorRowId, p.MetricInputStreamRowId,
                       p.ProcessorKey, s.MachineId, s.StreamKey
                FROM dbo.MetricAggregationProcessor p
                JOIN dbo.MetricInputStream s ON s.MetricInputStreamRowId = p.MetricInputStreamRowId
                WHERE p.ProcessorKeyBinary = @Key;
                """;
            exact.Parameters.Add("@Key", SqlDbType.VarBinary, 512).Value = OrdinalStringKeyCodec.Encode(cut.ProcessorId.Value);
            await using var reader = await exact.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new MissingContainmentHistoricalAuthorityException("The aggregation processor is absent.");
            processorRowId = reader.GetInt64(0);
            streamRowId = reader.GetInt64(1);
            if (reader.GetString(2) != cut.ProcessorId.Value || reader.GetGuid(3) != cut.StreamId.MachineId.Value ||
                reader.GetString(4) != cut.StreamId.StreamKey)
                throw new ContainmentHistoricalIntegrityException("The historical processor/stream binding contradicts the requested cut.");
        }
        await using (var exact = connection.CreateCommand())
        {
            exact.Transaction = transaction;
            exact.CommandText = "SELECT COUNT_BIG(*) FROM dbo.MetricAggregationRevision WHERE MetricAggregationProcessorRowId = @ProcessorRowId AND Position = @ThroughPosition;";
            Bind(exact, processorRowId, streamRowId, cut);
            if ((long)(await exact.ExecuteScalarAsync(cancellationToken))! != 1L)
                throw new MissingContainmentHistoricalAuthorityException("The exact aggregation revision is absent.");
        }
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Read the narrow contribution index in cut order, then seek each complete fact.
        // Keep both outer joins: missing/mismatched facts must reach the integrity checks.
        // Serial loop joins avoid a parallel exchange/hash join and a wide-row order sort.
        // This access-path candidate still requires representative restored-database plan validation.
        command.CommandText =
            "SELECT s.MachineId, s.StreamKey, f.Position, f.FactId, f.MetricInputKey, " +
            "f.MetricValue, f.Unit, f.StartsAtUtc, f.EndsAtUtc, f.CompanyId, f.SiteId, " +
            "f.ProductionLineId, f.MachineId, f.ShiftId, f.ShiftScheduleAssignmentId, " +
            "f.ProductionContextAssignmentId, f.ProductionOrderId, f.OperationId, f.PartId, " +
            "f.OperatorId, f.IsPlannedProductionTime, f.PlannedProductionScheduleAssignmentId, " +
            "f.SourceContextualizedActivityIntervalId, f.SourceEligibilityIntervalId, " +
            "f.SourceQuantityEvidenceId, f.OccurrenceSiteId, f.OccurrenceShiftScheduleAssignmentId, " +
            "f.OccurrenceShiftId, f.OccurrenceStartsAtUtc, f.OccurrenceEndsAtUtc, " +
            "f.ProductionDaySiteId, f.ProductionBusinessDate, c.Position, c.MetricInputStreamRowId " +
            "FROM dbo.MetricAggregationContribution c WITH (INDEX(UQ_MetricAggregationContribution_Position), FORCESEEK) " +
            "LEFT LOOP JOIN dbo.MetricInputFact f WITH (INDEX(PK_MetricInputFact), FORCESEEK) ON f.MetricInputFactRowId = c.MetricInputFactRowId AND f.MetricInputStreamRowId = c.MetricInputStreamRowId AND f.Position = c.Position " +
            "LEFT LOOP JOIN dbo.MetricInputStream s ON s.MetricInputStreamRowId = f.MetricInputStreamRowId " +
            "WHERE c.MetricAggregationProcessorRowId = @ProcessorRowId " +
            "AND c.Position <= @ThroughPosition " +
            "ORDER BY c.Position ASC OPTION (FORCE ORDER, MAXDOP 1);";
        Bind(command, processorRowId, streamRowId, cut);
        var inputs = new List<PositionedMetricInputFact>();
        await using var rows = await command.ExecuteReaderAsync(cancellationToken);
        while (await rows.ReadAsync(cancellationToken))
        {
            try
            {
                if (rows.IsDBNull(0))
                    throw new ContainmentHistoricalIntegrityException("An incorporated contribution has no matching fact/stream.");
                var input = SqlServerMetricAggregationStore.MaterializeHistoricalInput(rows);
                if (input.Position.Value == 0 || input.Position > cut.Position || input.StreamId != cut.StreamId ||
                    rows.GetDecimal(rows.FieldCount - 2) != (decimal)input.Position.Value ||
                    rows.GetInt64(rows.FieldCount - 1) != streamRowId)
                    throw new ContainmentHistoricalIntegrityException("Contribution contradicts its historical cut.");
                inputs.Add(input);
            }
            catch (Exception error) when (error is ArgumentException or InvalidDataException or FormatException or OverflowException)
            {
                throw new ContainmentHistoricalIntegrityException("Malformed historical contribution.", error);
            }
        }
        MetricAggregateContributionSet aggregates;
        try { aggregates = MetricInputContributionAggregator.Aggregate(cut.StreamId, inputs); }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or OverflowException)
        { throw new ContainmentHistoricalIntegrityException("Conflicting historical contribution membership.", error); }
        return new(inputs.AsReadOnly(), aggregates);
    }

    private static void Bind(SqlCommand command, long processor, long stream, MetricAggregationCheckpoint cut)
    {
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processor;
        command.Parameters.Add("@StreamRowId", SqlDbType.BigInt).Value = stream;
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@ThroughPosition", cut.Position.Value));
    }
}
