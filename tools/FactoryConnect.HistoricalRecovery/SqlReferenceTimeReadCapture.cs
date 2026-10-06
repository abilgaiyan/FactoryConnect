using System.Data;
using System.Text.Json;
using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.HistoricalRecovery;

/// <summary>Diagnostic-only SELECT/materialization equivalent to the private production outcome reader.</summary>
internal sealed class SqlReferenceTimeReadCapture(string connectionString)
{
    public async Task<IReadOnlyList<PublishedProductionReferenceTimeOutcome>> ReadAsync(
        MetricAggregationProcessorId processorId,
        ProductionReferenceTimeAuthorityRevision revision,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.MetricAggregationProcessorRowId
            FROM dbo.MetricAggregationProcessor p
            JOIN dbo.ProductionReferenceTimeRevision r
              ON r.MetricAggregationProcessorRowId = p.MetricAggregationProcessorRowId
            WHERE p.ProcessorKey = @Processor AND r.ProductionReferenceTimeRevision = @Revision;
            """;
        command.Parameters.Add("@Processor", SqlDbType.NVarChar, 256).Value = processorId.Value;
        var parameter = command.Parameters.Add("@Revision", SqlDbType.Decimal);
        parameter.Precision = 20;
        parameter.Scale = 0;
        parameter.Value = checked((decimal)revision.Value);
        var row = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (row is not long processor)
        {
            throw new InvalidDataException("Exact completed reference-time cut is unavailable.");
        }

        command.Parameters.Clear();
        command.Parameters.Add("@Processor", SqlDbType.BigInt).Value = processor;
        parameter = command.Parameters.Add("@Revision", SqlDbType.Decimal);
        parameter.Precision = 20;
        parameter.Scale = 0;
        parameter.Value = checked((decimal)revision.Value);
        command.CommandText = """
            SELECT o.ProductionReferenceTimeRevision, o.SourceQuantityEvidenceId,
                   o.CompanyId, o.SiteId, o.MachineId, o.ShiftScheduleAssignmentId,
                   o.ShiftId, o.ShiftStartsAtUtc, o.ShiftEndsAtUtc,
                   o.ProductionBusinessDate, o.PartId, o.OperationId,
                   o.OccurredAtUtc, o.ProducedQuantity, o.ProductionStandardAuthorityRevision,
                   o.ResolutionStatus, o.SelectedStandardVersionId,
                   o.SelectedStandardSourceReference, o.IdealProductionDurationSeconds,
                   (SELECT c.ConflictingStandardVersionId
                    FROM dbo.ProductionReferenceTimeOutcomeConflict c
                    WHERE c.MetricAggregationProcessorRowId = o.MetricAggregationProcessorRowId
                      AND c.ProductionReferenceTimeRevision = o.ProductionReferenceTimeRevision
                      AND c.SourceQuantityEvidenceId = o.SourceQuantityEvidenceId
                    ORDER BY c.ConflictingStandardVersionId FOR JSON PATH) AS ConflictsJson
            FROM dbo.ProductionReferenceTimeOutcome o
            WHERE o.MetricAggregationProcessorRowId = @Processor
              AND o.ProductionReferenceTimeRevision <= @Revision
            ORDER BY o.ProductionReferenceTimeRevision, o.SourceQuantityEvidenceId;
            """;
        var result = new List<PublishedProductionReferenceTimeOutcome>();
        await using var rows = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            using var conflicts = JsonDocument.Parse(rows.GetString(19));
            var site = new SiteId(rows.GetString(3));
            var resolution = new ProductionReferenceTimeResolution
            {
                SourceQuantityEvidenceId = new ProductionQuantityEvidenceId(rows.GetString(1)),
                CompanyId = new CompanyId(rows.GetString(2)), SiteId = site,
                MachineId = new MachineId(rows.GetGuid(4)),
                ShiftOccurrenceId = new ShiftOccurrenceId(site, new ShiftScheduleAssignmentId(rows.GetString(5)),
                    new ShiftId(rows.GetString(6)), rows.GetDateTimeOffset(7), rows.GetDateTimeOffset(8)),
                ProductionDayId = new ProductionDayId(site, DateOnly.FromDateTime(rows.GetDateTime(9))),
                PartId = rows.IsDBNull(10) ? null : new PartId(rows.GetString(10)),
                OperationId = rows.IsDBNull(11) ? null : new OperationId(rows.GetString(11)),
                OccurredAtUtc = rows.GetDateTimeOffset(12), ProducedUnits = rows.GetInt32(13),
                AuthorityRevision = checked((long)rows.GetDecimal(14)),
                Status = (ProductionReferenceTimeResolutionStatus)rows.GetByte(15),
                SelectedStandardVersionId = rows.IsDBNull(16) ? null : rows.GetString(16),
                SelectedStandardSourceReference = rows.IsDBNull(17) ? null : rows.GetString(17),
                IdealDurationSeconds = rows.IsDBNull(18) ? null : rows.GetDecimal(18),
                ConflictingStandardVersionIds = conflicts.RootElement.EnumerateArray()
                    .Select(value => value.GetProperty("ConflictingStandardVersionId").GetString()
                        ?? throw new InvalidDataException("Null conflict identity.")).ToArray(),
            };
            result.Add(new PublishedProductionReferenceTimeOutcome(
                new ProductionReferenceTimeAuthorityRevision(checked((long)rows.GetDecimal(0))), resolution));
        }

        return result;
    }
}
