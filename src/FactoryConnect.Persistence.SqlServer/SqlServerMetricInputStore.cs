using System.Data;
using System.Globalization;
using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed class SqlServerMetricInputStore :
    IMetricInputAppender,
    IMetricInputReader
{
    private readonly string _connectionString;

    public SqlServerMetricInputStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async ValueTask<PositionedMetricInputFact> AppendAsync(
        DurableMetricInputAppend append,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(append);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var sqlTransaction = (SqlTransaction)transaction;

        try
        {
            var result = await SqlServerMetricInputTransactionWriter.AppendAsync(
                connection,
                sqlTransaction,
                append,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async ValueTask<MetricInputReadBatch> ReadAsync(
        MetricInputReadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var streamRowId = await FindStreamAsync(
            connection,
            transaction: null,
            request.StreamId,
            cancellationToken);

        if (streamRowId is null)
        {
            if (request.AfterPosition is not null)
            {
                throw new InvalidOperationException(
                    "Metric input read position is beyond the durable stream tail.");
            }

            return new MetricInputReadBatch(
                request.StreamId,
                request.AfterPosition,
                throughPosition: null,
                []);
        }

        var tail = await ReadTailPositionAsync(
            connection,
            transaction: null,
            streamRowId.Value,
            cancellationToken);

        if (request.AfterPosition is not null &&
            (tail is null || request.AfterPosition > tail))
        {
            throw new InvalidOperationException(
                "Metric input read position is beyond the durable stream tail.");
        }

        if (tail is null || request.AfterPosition == tail)
        {
            return new MetricInputReadBatch(
                request.StreamId,
                request.AfterPosition,
                request.AfterPosition,
                []);
        }

        var facts = await ReadWindowAsync(
            connection,
            streamRowId.Value,
            request,
            cancellationToken);

        var throughPosition = facts.Count == 0
            ? request.AfterPosition
            : facts[^1].Position;

        return new MetricInputReadBatch(
            request.StreamId,
            request.AfterPosition,
            throughPosition,
            facts);
    }

    private static async Task<long?> FindStreamAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        MetricInputStreamId streamId,
        CancellationToken cancellationToken,
        bool lockForUpdate = false)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var hint = lockForUpdate ? " WITH (UPDLOCK, HOLDLOCK)" : string.Empty;
        command.CommandText =
            "SELECT MetricInputStreamRowId FROM dbo.MetricInputStream" + hint +
            " WHERE MachineId = @MachineId AND StreamKeyBinary = @StreamKeyBinary;";
        command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value =
            streamId.MachineId.Value;
        command.Parameters.Add("@StreamKeyBinary", SqlDbType.VarBinary, 512).Value =
            OrdinalStringKeyCodec.Encode(streamId.StreamKey);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null || result is DBNull
            ? null
            : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task<MetricInputPosition?> ReadTailPositionAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        long streamRowId,
        CancellationToken cancellationToken,
        bool lockForUpdate = false)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var hint = lockForUpdate ? " WITH (UPDLOCK, HOLDLOCK)" : string.Empty;
        command.CommandText =
            "SELECT MAX(Position) FROM dbo.MetricInputFact" + hint +
            " WHERE MetricInputStreamRowId = @StreamRowId;";
        command.Parameters.Add("@StreamRowId", SqlDbType.BigInt).Value = streamRowId;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null || result is DBNull)
        {
            return null;
        }

        return new MetricInputPosition(
            SqlServerUInt64.Materialize((decimal)result));
    }

    private static async Task<List<PositionedMetricInputFact>> ReadWindowAsync(
        SqlConnection connection,
        long streamRowId,
        MetricInputReadRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = CreateFactReadCommand(connection, transaction: null);
        command.CommandText +=
            " WHERE f.MetricInputStreamRowId = @StreamRowId " +
            "AND f.Position > @AfterPosition " +
            "ORDER BY f.Position ASC OFFSET 0 ROWS FETCH NEXT @MaxCount ROWS ONLY;";
        command.Parameters.Add("@StreamRowId", SqlDbType.BigInt).Value = streamRowId;
        command.Parameters.Add(SqlServerUInt64.CreateParameter(
            "@AfterPosition",
            request.AfterPosition?.Value ?? 0UL));
        command.Parameters.Add("@MaxCount", SqlDbType.Int).Value = request.MaxCount;

        var result = new List<PositionedMetricInputFact>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(Materialize(reader));
        }

        return result;
    }

    private static SqlCommand CreateFactReadCommand(
        SqlConnection connection,
        SqlTransaction? transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT s.MachineId, s.StreamKey, f.Position, f.FactId, f.MetricInputKey, " +
            "f.MetricValue, f.Unit, f.StartsAtUtc, f.EndsAtUtc, f.CompanyId, f.SiteId, " +
            "f.ProductionLineId, f.MachineId, f.ShiftId, f.ShiftScheduleAssignmentId, " +
            "f.ProductionContextAssignmentId, f.ProductionOrderId, f.OperationId, f.PartId, " +
            "f.OperatorId, f.IsPlannedProductionTime, f.PlannedProductionScheduleAssignmentId, " +
            "f.SourceContextualizedActivityIntervalId, f.SourceEligibilityIntervalId, " +
            "f.SourceQuantityEvidenceId, f.OccurrenceSiteId, " +
            "f.OccurrenceShiftScheduleAssignmentId, f.OccurrenceShiftId, " +
            "f.OccurrenceStartsAtUtc, f.OccurrenceEndsAtUtc, f.ProductionDaySiteId, " +
            "f.ProductionBusinessDate FROM dbo.MetricInputFact f " +
            "JOIN dbo.MetricInputStream s ON s.MetricInputStreamRowId = f.MetricInputStreamRowId";
        return command;
    }

    private static PositionedMetricInputFact Materialize(SqlDataReader reader)
    {
        var ordinal = 0;
        var streamMachineId = new MachineId(reader.GetGuid(ordinal++));
        var streamKey = reader.GetString(ordinal++);
        var position = new MetricInputPosition(
            SqlServerUInt64.Materialize(reader.GetDecimal(ordinal++)));
        var factId = new MetricInputFactId(reader.GetString(ordinal++));
        var key = reader.GetString(ordinal++);
        var value = SqlServerCanonicalDecimalCodec.Deserialize(reader.GetString(ordinal++));
        var unit = reader.GetString(ordinal++);
        var startsAtUtc = reader.GetDateTimeOffset(ordinal++);
        var endsAtUtc = reader.GetDateTimeOffset(ordinal++);
        var companyId = new CompanyId(reader.GetString(ordinal++));
        var siteId = new SiteId(reader.GetString(ordinal++));
        var productionLineId = ReadNullable(reader, ordinal++, value => new ProductionLineId(value));
        var machineId = new MachineId(reader.GetGuid(ordinal++));
        var shiftId = new ShiftId(reader.GetString(ordinal++));
        var scheduleId = new ShiftScheduleAssignmentId(reader.GetString(ordinal++));
        var contextId = ReadNullable(reader, ordinal++, value => new ProductionContextAssignmentId(value));
        var orderId = ReadNullable(reader, ordinal++, value => new ProductionOrderId(value));
        var operationId = ReadNullable(reader, ordinal++, value => new OperationId(value));
        var partId = ReadNullable(reader, ordinal++, value => new PartId(value));
        var operatorId = ReadNullable(reader, ordinal++, value => new OperatorId(value));
        bool? planned = reader.IsDBNull(ordinal) ? null : reader.GetBoolean(ordinal);
        ordinal++;
        var plannedAssignmentId = ReadNullable(
            reader,
            ordinal++,
            value => new PlannedProductionScheduleAssignmentId(value));
        var sourceContextId = ReadNullable(
            reader,
            ordinal++,
            value => new ContextualizedActivityIntervalId(value));
        var sourceEligibilityId = ReadNullable(
            reader,
            ordinal++,
            value => new ProductionTimeEligibilityIntervalId(value));
        var sourceQuantityId = ReadNullable(
            reader,
            ordinal++,
            value => new ProductionQuantityEvidenceId(value));
        var occurrenceSiteId = new SiteId(reader.GetString(ordinal++));
        var occurrenceScheduleId = new ShiftScheduleAssignmentId(reader.GetString(ordinal++));
        var occurrenceShiftId = new ShiftId(reader.GetString(ordinal++));
        var occurrenceStartsAtUtc = reader.GetDateTimeOffset(ordinal++);
        var occurrenceEndsAtUtc = reader.GetDateTimeOffset(ordinal++);
        var productionDaySiteId = new SiteId(reader.GetString(ordinal++));
        var productionBusinessDate = DateOnly.FromDateTime(reader.GetDateTime(ordinal));

        var fact = new DurableMetricInputFact
        {
            Id = factId,
            Key = key,
            Value = value,
            Unit = unit,
            StartsAtUtc = startsAtUtc,
            EndsAtUtc = endsAtUtc,
            CompanyId = companyId,
            SiteId = siteId,
            ProductionLineId = productionLineId,
            MachineId = machineId,
            ShiftId = shiftId,
            ShiftScheduleAssignmentId = scheduleId,
            ProductionContextAssignmentId = contextId,
            ProductionOrderId = orderId,
            OperationId = operationId,
            PartId = partId,
            OperatorId = operatorId,
            IsPlannedProductionTime = planned,
            PlannedProductionScheduleAssignmentId = plannedAssignmentId,
            SourceContextualizedActivityIntervalId = sourceContextId,
            SourceEligibilityIntervalId = sourceEligibilityId,
            SourceQuantityEvidenceId = sourceQuantityId,
        };

        return new PositionedMetricInputFact(
            new MetricInputStreamId(streamMachineId, streamKey),
            position,
            fact,
            new ShiftOccurrenceId(
                occurrenceSiteId,
                occurrenceScheduleId,
                occurrenceShiftId,
                occurrenceStartsAtUtc,
                occurrenceEndsAtUtc),
            new ProductionDayId(
                productionDaySiteId,
                productionBusinessDate));
    }

    private static T? ReadNullable<T>(
        SqlDataReader reader,
        int ordinal,
        Func<string, T> factory)
        where T : struct =>
        reader.IsDBNull(ordinal) ? null : factory(reader.GetString(ordinal));
}
