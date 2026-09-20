using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed partial class SqlServerMetricAggregationStore
{
    public async ValueTask<OperationalMetricComponentSnapshot> ReadAtRevisionAsync(
        OperationalMetricComponentSnapshotRequest request,
        MetricAggregationCheckpoint requiredRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(requiredRevision);

        if (requiredRevision.ProcessorId != request.ProcessorId ||
            requiredRevision.StreamId.MachineId != request.EvaluationKey.MachineId)
        {
            throw new ArgumentException(
                "Required aggregation revision must belong to the snapshot processor and machine stream.",
                nameof(requiredRevision));
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var processor = await FindProcessorAsync(
            connection, transaction: null, requiredRevision.ProcessorId, cancellationToken);
        if (processor is null)
        {
            throw HistoricalRevisionUnavailable();
        }

        await ValidateProcessorStreamAsync(
            connection, processor.Value.StreamRowId, requiredRevision.StreamId, cancellationToken);

        if (!await RevisionExistsAsync(
                connection,
                transaction: null,
                processor.Value.RowId,
                requiredRevision.Position,
                cancellationToken))
        {
            throw HistoricalRevisionUnavailable();
        }

        var inputs = await ReadHistoricalContributionsAsync(
            connection,
            processor.Value.RowId,
            processor.Value.StreamRowId,
            requiredRevision.Position,
            cancellationToken);
        var contributionSet = MetricInputContributionAggregator.Aggregate(
            requiredRevision.StreamId,
            inputs);
        var components = new List<OperationalMetricComponent>(request.Operands.Count);

        foreach (var operand in request.Operands)
        {
            var source = (OperationalMetricOperandSource.Component)operand.Source;
            var aggregate = ReadHistoricalAggregate(
                contributionSet,
                request.EvaluationKey,
                source.ComponentKey);
            if (aggregate is null)
            {
                continue;
            }

            components.Add(new OperationalMetricComponent(
                operand.OperandName,
                new OperationalMetricAggregateSourceIdentity(
                    request.ProcessorId,
                    request.EvaluationKey.MachineId,
                    request.EvaluationKey.PeriodId,
                    source.ComponentKey),
                operand.RequiredDimension,
                aggregate));
        }

        return new OperationalMetricComponentSnapshot(
            request.EvaluationKey,
            requiredRevision,
            components);
    }

    private static InvalidOperationException HistoricalRevisionUnavailable() =>
        new("Requested historical aggregation revision is not available.");

    private static MetricAggregateValue? ReadHistoricalAggregate(
        MetricAggregateContributionSet contributionSet,
        OperationalMetricEvaluationKey evaluationKey,
        string componentKey) =>
        evaluationKey.PeriodId switch
        {
            OperationalMetricPeriodId.Shift shift => contributionSet.ShiftContributions
                .FirstOrDefault(contribution => contribution.Key == new ShiftMetricAggregateKey(
                    evaluationKey.MachineId,
                    shift.ShiftOccurrenceId,
                    componentKey))
                ?.Value,
            OperationalMetricPeriodId.ProductionDay productionDay => contributionSet.ProductionDayContributions
                .FirstOrDefault(contribution => contribution.Key == new ProductionDayMetricAggregateKey(
                    evaluationKey.MachineId,
                    productionDay.ProductionDayId,
                    componentKey))
                ?.Value,
            _ => throw new InvalidOperationException("Unsupported operational metric period type."),
        };

    private static async Task<IReadOnlyList<PositionedMetricInputFact>> ReadHistoricalContributionsAsync(
        SqlConnection connection,
        long processorRowId,
        long streamRowId,
        MetricInputPosition throughPosition,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT s.MachineId, s.StreamKey, f.Position, f.FactId, f.MetricInputKey, " +
            "f.MetricValue, f.Unit, f.StartsAtUtc, f.EndsAtUtc, f.CompanyId, f.SiteId, " +
            "f.ProductionLineId, f.MachineId, f.ShiftId, f.ShiftScheduleAssignmentId, " +
            "f.ProductionContextAssignmentId, f.ProductionOrderId, f.OperationId, f.PartId, " +
            "f.OperatorId, f.IsPlannedProductionTime, f.PlannedProductionScheduleAssignmentId, " +
            "f.SourceContextualizedActivityIntervalId, f.SourceEligibilityIntervalId, " +
            "f.SourceQuantityEvidenceId, f.OccurrenceSiteId, f.OccurrenceShiftScheduleAssignmentId, " +
            "f.OccurrenceShiftId, f.OccurrenceStartsAtUtc, f.OccurrenceEndsAtUtc, " +
            "f.ProductionDaySiteId, f.ProductionBusinessDate " +
            "FROM dbo.MetricAggregationContribution c " +
            "JOIN dbo.MetricInputFact f ON f.MetricInputFactRowId = c.MetricInputFactRowId " +
            "JOIN dbo.MetricInputStream s ON s.MetricInputStreamRowId = f.MetricInputStreamRowId " +
            "WHERE c.MetricAggregationProcessorRowId = @ProcessorRowId " +
            "AND c.MetricInputStreamRowId = @StreamRowId AND c.Position <= @ThroughPosition " +
            "ORDER BY c.Position ASC;";
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
        command.Parameters.Add("@StreamRowId", SqlDbType.BigInt).Value = streamRowId;
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@ThroughPosition", throughPosition.Value));

        var inputs = new List<PositionedMetricInputFact>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            inputs.Add(MaterializeHistoricalInput(reader));
        }

        return inputs;
    }

    private static PositionedMetricInputFact MaterializeHistoricalInput(SqlDataReader reader)
    {
        var ordinal = 0;
        var streamMachineId = new MachineId(reader.GetGuid(ordinal++));
        var streamKey = reader.GetString(ordinal++);
        var position = new MetricInputPosition(SqlServerUInt64.Materialize(reader.GetDecimal(ordinal++)));
        var factId = new MetricInputFactId(reader.GetString(ordinal++));
        var key = reader.GetString(ordinal++);
        var value = SqlServerCanonicalDecimalCodec.Deserialize(reader.GetString(ordinal++));
        var unit = reader.GetString(ordinal++);
        var startsAtUtc = reader.GetDateTimeOffset(ordinal++);
        var endsAtUtc = reader.GetDateTimeOffset(ordinal++);
        var companyId = new CompanyId(reader.GetString(ordinal++));
        var siteId = new SiteId(reader.GetString(ordinal++));
        var productionLineId = ReadNullableValue(reader, ordinal++, static value => new ProductionLineId(value));
        var machineId = new MachineId(reader.GetGuid(ordinal++));
        var shiftId = new ShiftId(reader.GetString(ordinal++));
        var scheduleId = new ShiftScheduleAssignmentId(reader.GetString(ordinal++));
        var contextId = ReadNullableValue(reader, ordinal++, static value => new ProductionContextAssignmentId(value));
        var orderId = ReadNullableValue(reader, ordinal++, static value => new ProductionOrderId(value));
        var operationId = ReadNullableValue(reader, ordinal++, static value => new OperationId(value));
        var partId = ReadNullableValue(reader, ordinal++, static value => new PartId(value));
        var operatorId = ReadNullableValue(reader, ordinal++, static value => new OperatorId(value));
        bool? planned = reader.IsDBNull(ordinal) ? null : reader.GetBoolean(ordinal);
        ordinal++;
        var plannedAssignmentId = ReadNullableValue(reader, ordinal++, static value => new PlannedProductionScheduleAssignmentId(value));
        var sourceContextId = ReadNullableValue(reader, ordinal++, static value => new ContextualizedActivityIntervalId(value));
        var sourceEligibilityId = ReadNullableValue(reader, ordinal++, static value => new ProductionTimeEligibilityIntervalId(value));
        var sourceQuantityId = ReadNullableValue(reader, ordinal++, static value => new ProductionQuantityEvidenceId(value));
        var occurrenceSiteId = new SiteId(reader.GetString(ordinal++));
        var occurrenceScheduleId = new ShiftScheduleAssignmentId(reader.GetString(ordinal++));
        var occurrenceShiftId = new ShiftId(reader.GetString(ordinal++));
        var occurrenceStartsAtUtc = reader.GetDateTimeOffset(ordinal++);
        var occurrenceEndsAtUtc = reader.GetDateTimeOffset(ordinal++);
        var productionDaySiteId = new SiteId(reader.GetString(ordinal++));
        var productionBusinessDate = DateOnly.FromDateTime(reader.GetDateTime(ordinal));

        return new PositionedMetricInputFact(
            new MetricInputStreamId(streamMachineId, streamKey),
            position,
            new DurableMetricInputFact
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
            },
            new ShiftOccurrenceId(
                occurrenceSiteId,
                occurrenceScheduleId,
                occurrenceShiftId,
                occurrenceStartsAtUtc,
                occurrenceEndsAtUtc),
            new ProductionDayId(productionDaySiteId, productionBusinessDate));
    }

    private static T? ReadNullableValue<T>(
        SqlDataReader reader,
        int ordinal,
        Func<string, T> factory)
        where T : struct =>
        reader.IsDBNull(ordinal) ? null : factory(reader.GetString(ordinal));
}
