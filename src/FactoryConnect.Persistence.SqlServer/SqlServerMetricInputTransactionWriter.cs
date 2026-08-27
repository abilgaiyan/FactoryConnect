using System.Data;
using System.Globalization;
using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlServerMetricInputTransactionWriter
{
    public static async Task<PositionedMetricInputFact> AppendAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        DurableMetricInputAppend append,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(append);

        var streamRowId = await GetOrCreateStreamAsync(
            connection,
            transaction,
            append.StreamId,
            cancellationToken);

        var existingPosition = await ReadExistingPositionAsync(
            connection,
            transaction,
            streamRowId,
            append.Fact.Id,
            cancellationToken);

        if (existingPosition is not null)
        {
            if (!await IsExactMatchAsync(
                    connection,
                    transaction,
                    streamRowId,
                    existingPosition,
                    append,
                    cancellationToken))
            {
                throw new InvalidOperationException(
                    "A durable metric input fact identity was reused with a conflicting payload.");
            }

            return new PositionedMetricInputFact(
                append.StreamId,
                existingPosition,
                append.Fact,
                append.ShiftOccurrenceId,
                append.ProductionDayId);
        }

        var nextPosition = await AllocateNextPositionAsync(
            connection,
            transaction,
            streamRowId,
            cancellationToken);

        await InsertFactAsync(
            connection,
            transaction,
            streamRowId,
            nextPosition,
            append,
            cancellationToken);

        return new PositionedMetricInputFact(
            append.StreamId,
            nextPosition,
            append.Fact,
            append.ShiftOccurrenceId,
            append.ProductionDayId);
    }

    private static async Task<long> GetOrCreateStreamAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        MetricInputStreamId streamId,
        CancellationToken cancellationToken)
    {
        await using var find = connection.CreateCommand();
        find.Transaction = transaction;
        find.CommandText =
            "SELECT MetricInputStreamRowId FROM dbo.MetricInputStream WITH (UPDLOCK, HOLDLOCK) " +
            "WHERE MachineId = @MachineId AND StreamKeyBinary = @StreamKeyBinary;";
        find.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value = streamId.MachineId.Value;
        find.Parameters.Add("@StreamKeyBinary", SqlDbType.VarBinary, 512).Value =
            OrdinalStringKeyCodec.Encode(streamId.StreamKey);
        var existing = await find.ExecuteScalarAsync(cancellationToken);
        if (existing is not null && existing is not DBNull)
        {
            return Convert.ToInt64(existing, CultureInfo.InvariantCulture);
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            "INSERT INTO dbo.MetricInputStream (MachineId, StreamKeyBinary, StreamKey) " +
            "OUTPUT INSERTED.MetricInputStreamRowId " +
            "VALUES (@MachineId, @StreamKeyBinary, @StreamKey);";
        insert.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value = streamId.MachineId.Value;
        insert.Parameters.Add("@StreamKeyBinary", SqlDbType.VarBinary, 512).Value =
            OrdinalStringKeyCodec.Encode(streamId.StreamKey);
        AddString(insert, "@StreamKey", streamId.StreamKey, 256);
        return Convert.ToInt64(
            await insert.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    private static async Task<MetricInputPosition?> ReadExistingPositionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long streamRowId,
        MetricInputFactId factId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT Position FROM dbo.MetricInputFact WITH (UPDLOCK, HOLDLOCK) " +
            "WHERE MetricInputStreamRowId = @StreamRowId AND FactIdBinary = @FactIdBinary;";
        command.Parameters.Add("@StreamRowId", SqlDbType.BigInt).Value = streamRowId;
        command.Parameters.Add("@FactIdBinary", SqlDbType.VarBinary, 512).Value =
            OrdinalStringKeyCodec.Encode(factId.Value);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null || result is DBNull
            ? null
            : new MetricInputPosition(SqlServerUInt64.Materialize((decimal)result));
    }

    private static async Task<MetricInputPosition> AllocateNextPositionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long streamRowId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT MAX(Position) FROM dbo.MetricInputFact WITH (UPDLOCK, HOLDLOCK) " +
            "WHERE MetricInputStreamRowId = @StreamRowId;";
        command.Parameters.Add("@StreamRowId", SqlDbType.BigInt).Value = streamRowId;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        var next = result is null || result is DBNull
            ? 1UL
            : checked(SqlServerUInt64.Materialize((decimal)result) + 1UL);
        return new MetricInputPosition(next);
    }

    private static async Task<bool> IsExactMatchAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long streamRowId,
        MetricInputPosition position,
        DurableMetricInputAppend append,
        CancellationToken cancellationToken)
    {
        var fact = append.Fact;
        var occurrence = append.ShiftOccurrenceId;
        var productionDay = append.ProductionDayId;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT COUNT_BIG(1) FROM dbo.MetricInputFact " +
            "WHERE MetricInputStreamRowId = @StreamRowId AND Position = @Position " +
            "AND FactIdBinary = @FactIdBinary AND FactId = @FactId " +
            "AND MetricInputKey = @MetricInputKey AND MetricValue = @MetricValue AND Unit = @Unit " +
            "AND StartsAtUtc = @StartsAtUtc AND EndsAtUtc = @EndsAtUtc " +
            "AND CompanyId = @CompanyId AND SiteId = @SiteId AND MachineId = @MachineId " +
            "AND ShiftId = @ShiftId AND ShiftScheduleAssignmentId = @ScheduleId " +
            "AND ((ProductionLineId = @ProductionLineId) OR (ProductionLineId IS NULL AND @ProductionLineId IS NULL)) " +
            "AND ((ProductionContextAssignmentId = @ContextId) OR (ProductionContextAssignmentId IS NULL AND @ContextId IS NULL)) " +
            "AND ((ProductionOrderId = @OrderId) OR (ProductionOrderId IS NULL AND @OrderId IS NULL)) " +
            "AND ((OperationId = @OperationId) OR (OperationId IS NULL AND @OperationId IS NULL)) " +
            "AND ((PartId = @PartId) OR (PartId IS NULL AND @PartId IS NULL)) " +
            "AND ((OperatorId = @OperatorId) OR (OperatorId IS NULL AND @OperatorId IS NULL)) " +
            "AND ((IsPlannedProductionTime = @IsPlanned) OR (IsPlannedProductionTime IS NULL AND @IsPlanned IS NULL)) " +
            "AND ((PlannedProductionScheduleAssignmentId = @PlannedAssignmentId) OR (PlannedProductionScheduleAssignmentId IS NULL AND @PlannedAssignmentId IS NULL)) " +
            "AND ((SourceContextualizedActivityIntervalId = @SourceContextId) OR (SourceContextualizedActivityIntervalId IS NULL AND @SourceContextId IS NULL)) " +
            "AND ((SourceEligibilityIntervalId = @SourceEligibilityId) OR (SourceEligibilityIntervalId IS NULL AND @SourceEligibilityId IS NULL)) " +
            "AND ((SourceQuantityEvidenceId = @SourceQuantityId) OR (SourceQuantityEvidenceId IS NULL AND @SourceQuantityId IS NULL)) " +
            "AND OccurrenceSiteId = @OccurrenceSiteId " +
            "AND OccurrenceShiftScheduleAssignmentId = @OccurrenceScheduleId " +
            "AND OccurrenceShiftId = @OccurrenceShiftId " +
            "AND OccurrenceStartsAtUtc = @OccurrenceStartsAtUtc " +
            "AND OccurrenceEndsAtUtc = @OccurrenceEndsAtUtc " +
            "AND ProductionDaySiteId = @ProductionDaySiteId " +
            "AND ProductionBusinessDate = @ProductionBusinessDate;";
        AddFactParameters(command, streamRowId, position, append);
        var count = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
        return count == 1;
    }

    private static async Task InsertFactAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long streamRowId,
        MetricInputPosition position,
        DurableMetricInputAppend append,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO dbo.MetricInputFact " +
            "(MetricInputStreamRowId, Position, FactIdBinary, FactId, MetricInputKey, MetricValue, Unit, " +
            "StartsAtUtc, EndsAtUtc, CompanyId, SiteId, ProductionLineId, MachineId, ShiftId, " +
            "ShiftScheduleAssignmentId, ProductionContextAssignmentId, ProductionOrderId, OperationId, " +
            "PartId, OperatorId, IsPlannedProductionTime, PlannedProductionScheduleAssignmentId, " +
            "SourceContextualizedActivityIntervalId, SourceEligibilityIntervalId, SourceQuantityEvidenceId, " +
            "OccurrenceSiteId, OccurrenceShiftScheduleAssignmentId, OccurrenceShiftId, " +
            "OccurrenceStartsAtUtc, OccurrenceEndsAtUtc, ProductionDaySiteId, ProductionBusinessDate) " +
            "VALUES (@StreamRowId, @Position, @FactIdBinary, @FactId, @MetricInputKey, @MetricValue, @Unit, " +
            "@StartsAtUtc, @EndsAtUtc, @CompanyId, @SiteId, @ProductionLineId, @MachineId, @ShiftId, " +
            "@ScheduleId, @ContextId, @OrderId, @OperationId, @PartId, @OperatorId, @IsPlanned, " +
            "@PlannedAssignmentId, @SourceContextId, @SourceEligibilityId, @SourceQuantityId, " +
            "@OccurrenceSiteId, @OccurrenceScheduleId, @OccurrenceShiftId, @OccurrenceStartsAtUtc, " +
            "@OccurrenceEndsAtUtc, @ProductionDaySiteId, @ProductionBusinessDate);";
        AddFactParameters(command, streamRowId, position, append);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddFactParameters(
        SqlCommand command,
        long streamRowId,
        MetricInputPosition position,
        DurableMetricInputAppend append)
    {
        var fact = append.Fact;
        var occurrence = append.ShiftOccurrenceId;
        var productionDay = append.ProductionDayId;

        command.Parameters.Add("@StreamRowId", SqlDbType.BigInt).Value = streamRowId;
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@Position", position.Value));
        command.Parameters.Add("@FactIdBinary", SqlDbType.VarBinary, 512).Value =
            OrdinalStringKeyCodec.Encode(fact.Id.Value);
        AddString(command, "@FactId", fact.Id.Value, 256);
        AddString(command, "@MetricInputKey", fact.Key, 256);
        AddString(command, "@MetricValue", SqlServerCanonicalDecimalCodec.Serialize(fact.Value), 64);
        AddString(command, "@Unit", fact.Unit, 128);
        command.Parameters.Add("@StartsAtUtc", SqlDbType.DateTimeOffset).Value = fact.StartsAtUtc;
        command.Parameters.Add("@EndsAtUtc", SqlDbType.DateTimeOffset).Value = fact.EndsAtUtc;
        AddString(command, "@CompanyId", fact.CompanyId.Value, 256);
        AddString(command, "@SiteId", fact.SiteId.Value, 256);
        AddNullableString(command, "@ProductionLineId", fact.ProductionLineId?.Value);
        command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value = fact.MachineId.Value;
        AddString(command, "@ShiftId", fact.ShiftId.Value, 256);
        AddString(command, "@ScheduleId", fact.ShiftScheduleAssignmentId!.Value.Value, 256);
        AddNullableString(command, "@ContextId", fact.ProductionContextAssignmentId?.Value);
        AddNullableString(command, "@OrderId", fact.ProductionOrderId?.Value);
        AddNullableString(command, "@OperationId", fact.OperationId?.Value);
        AddNullableString(command, "@PartId", fact.PartId?.Value);
        AddNullableString(command, "@OperatorId", fact.OperatorId?.Value);
        command.Parameters.Add("@IsPlanned", SqlDbType.Bit).Value =
            fact.IsPlannedProductionTime is null ? DBNull.Value : fact.IsPlannedProductionTime.Value;
        AddNullableString(command, "@PlannedAssignmentId", fact.PlannedProductionScheduleAssignmentId?.Value);
        AddNullableString(command, "@SourceContextId", fact.SourceContextualizedActivityIntervalId?.Value);
        AddNullableString(command, "@SourceEligibilityId", fact.SourceEligibilityIntervalId?.Value);
        AddNullableString(command, "@SourceQuantityId", fact.SourceQuantityEvidenceId?.Value);
        AddString(command, "@OccurrenceSiteId", occurrence.SiteId.Value, 256);
        AddString(command, "@OccurrenceScheduleId", occurrence.ShiftScheduleAssignmentId.Value, 256);
        AddString(command, "@OccurrenceShiftId", occurrence.ShiftId.Value, 256);
        command.Parameters.Add("@OccurrenceStartsAtUtc", SqlDbType.DateTimeOffset).Value = occurrence.StartsAtUtc;
        command.Parameters.Add("@OccurrenceEndsAtUtc", SqlDbType.DateTimeOffset).Value = occurrence.EndsAtUtc;
        AddString(command, "@ProductionDaySiteId", productionDay.SiteId.Value, 256);
        command.Parameters.Add("@ProductionBusinessDate", SqlDbType.Date).Value =
            productionDay.BusinessDate.ToDateTime(TimeOnly.MinValue);
    }

    private static void AddString(SqlCommand command, string name, string value, int size) =>
        command.Parameters.Add(name, SqlDbType.NVarChar, size).Value = value;

    private static void AddNullableString(SqlCommand command, string name, string? value) =>
        command.Parameters.Add(name, SqlDbType.NVarChar, 256).Value =
            value is null ? DBNull.Value : value;
}
