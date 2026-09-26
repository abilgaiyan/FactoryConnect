using System.Data;
using System.Text.Json;
using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>Persists immutable source outcomes at an independently advancing authority cut.</summary>
public sealed class SqlServerProductionReferenceTimeOutcomeStore
{
    private readonly string _connectionString;

    public SqlServerProductionReferenceTimeOutcomeStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async Task<PublishedProductionReferenceTimeOutcome> PublishAsync(
        MetricAggregationProcessorId processorId,
        PublishedProductionReferenceTimeOutcome proposed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(proposed);
        proposed.Resolution.Validate();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

        var processorRowId = await ReadProcessorRowIdAsync(
            connection, transaction, processorId, cancellationToken);
        var latest = await ReadLatestRevisionAsync(
            connection, transaction, processorRowId, cancellationToken);
        var existing = await ReadSourceAsync(
            connection, transaction, processorRowId, proposed.SourceQuantityEvidenceId, cancellationToken);
        if (existing is not null)
        {
            if (!Equivalent(existing.Resolution, proposed.Resolution))
            {
                throw new InvalidOperationException(
                    "Ordinary replay cannot change a reference-time source outcome or its authority cut.");
            }

            await transaction.CommitAsync(cancellationToken);
            return existing;
        }

        if (latest != proposed.PublicationRevision.Value - 1)
        {
            throw new InvalidOperationException("Reference-time publication revision must advance by one.");
        }

        await using (var revision = connection.CreateCommand())
        {
            revision.Transaction = transaction;
            revision.CommandText = """
                INSERT INTO dbo.ProductionReferenceTimeRevision
                    (MetricAggregationProcessorRowId, ProductionReferenceTimeRevision)
                VALUES (@Processor, @Revision);
                """;
            revision.Parameters.Add("@Processor", SqlDbType.BigInt).Value = processorRowId;
            revision.Parameters.Add(SqlServerUInt64.CreateParameter(
                "@Revision", checked((ulong)proposed.PublicationRevision.Value)));
            await revision.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertOutcomeAsync(connection, transaction, processorRowId, proposed, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return proposed;
    }

    public async Task<IReadOnlyList<PublishedProductionReferenceTimeOutcome>> ReadAtRevisionAsync(
        MetricAggregationProcessorId processorId,
        ProductionReferenceTimeAuthorityRevision revision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var processorRowId = await ReadProcessorRowIdAsync(
            connection, null, processorId, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT_BIG(*) FROM dbo.ProductionReferenceTimeRevision
            WHERE MetricAggregationProcessorRowId = @Processor
              AND ProductionReferenceTimeRevision = @Revision;
            """;
        command.Parameters.Add("@Processor", SqlDbType.BigInt).Value = processorRowId;
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@Revision", checked((ulong)revision.Value)));
        if ((long)(await command.ExecuteScalarAsync(cancellationToken))! != 1)
        {
            throw new InvalidOperationException("Reference-time authority revision is not available.");
        }

        var result = new List<PublishedProductionReferenceTimeOutcome>();
        await using var outcomes = connection.CreateCommand();
        outcomes.CommandText = OutcomeSelect + " WHERE o.MetricAggregationProcessorRowId = @Processor AND o.ProductionReferenceTimeRevision <= @Revision ORDER BY o.ProductionReferenceTimeRevision, o.SourceQuantityEvidenceId;";
        outcomes.Parameters.Add("@Processor", SqlDbType.BigInt).Value = processorRowId;
        outcomes.Parameters.Add(SqlServerUInt64.CreateParameter("@Revision", checked((ulong)revision.Value)));
        await using var reader = await outcomes.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(Materialize(reader));
        }

        return result;
    }

    private static async Task<long> ReadProcessorRowIdAsync(
        SqlConnection connection, SqlTransaction? transaction,
        MetricAggregationProcessorId processorId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT MetricAggregationProcessorRowId FROM dbo.MetricAggregationProcessor WHERE ProcessorKey = @ProcessorKey;";
        command.Parameters.Add("@ProcessorKey", SqlDbType.NVarChar, 256).Value = processorId.Value;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is long id ? id : throw new InvalidOperationException("Aggregation authority is not available.");
    }

    private static async Task<long> ReadLatestRevisionAsync(
        SqlConnection connection, SqlTransaction transaction,
        long processorRowId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT MAX(ProductionReferenceTimeRevision) FROM dbo.ProductionReferenceTimeRevision WITH (UPDLOCK, HOLDLOCK) WHERE MetricAggregationProcessorRowId = @Processor;";
        command.Parameters.Add("@Processor", SqlDbType.BigInt).Value = processorRowId;
        return await command.ExecuteScalarAsync(cancellationToken) is decimal value
            ? checked((long)value)
            : throw new InvalidOperationException("Empty reference-time revision is not available.");
    }

    private static async Task<PublishedProductionReferenceTimeOutcome?> ReadSourceAsync(
        SqlConnection connection, SqlTransaction transaction, long processorRowId,
        ProductionQuantityEvidenceId sourceId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = OutcomeSelect + " WHERE o.MetricAggregationProcessorRowId = @Processor AND o.SourceQuantityEvidenceId = @Source;";
        command.Parameters.Add("@Processor", SqlDbType.BigInt).Value = processorRowId;
        command.Parameters.Add("@Source", SqlDbType.NVarChar, 256).Value = sourceId.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Materialize(reader) : null;
    }

    private const string OutcomeSelect = """
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
                ORDER BY c.ConflictingStandardVersionId
                FOR JSON PATH) AS ConflictsJson
        FROM dbo.ProductionReferenceTimeOutcome o
        """;

    private static PublishedProductionReferenceTimeOutcome Materialize(SqlDataReader reader)
    {
        using var conflictsJson = JsonDocument.Parse(reader.GetString(19));
        var conflicts = conflictsJson.RootElement.EnumerateArray()
            .Select(static item => item.GetProperty("ConflictingStandardVersionId").GetString()!)
            .ToArray();
        var site = new SiteId(reader.GetString(3));
        var shift = new ShiftOccurrenceId(site,
            new ShiftScheduleAssignmentId(reader.GetString(5)),
            new ShiftId(reader.GetString(6)), reader.GetDateTimeOffset(7), reader.GetDateTimeOffset(8));
        var resolution = new ProductionReferenceTimeResolution
        {
            SourceQuantityEvidenceId = new ProductionQuantityEvidenceId(reader.GetString(1)),
            CompanyId = new CompanyId(reader.GetString(2)),
            SiteId = site,
            MachineId = new MachineId(reader.GetGuid(4)),
            ShiftOccurrenceId = shift,
            ProductionDayId = new ProductionDayId(site, DateOnly.FromDateTime(reader.GetDateTime(9))),
            PartId = reader.IsDBNull(10) ? null : new PartId(reader.GetString(10)),
            OperationId = reader.IsDBNull(11) ? null : new OperationId(reader.GetString(11)),
            OccurredAtUtc = reader.GetDateTimeOffset(12),
            ProducedUnits = reader.GetInt32(13),
            AuthorityRevision = checked((long)reader.GetDecimal(14)),
            Status = (ProductionReferenceTimeResolutionStatus)reader.GetByte(15),
            SelectedStandardVersionId = reader.IsDBNull(16) ? null : reader.GetString(16),
            SelectedStandardSourceReference = reader.IsDBNull(17) ? null : reader.GetString(17),
            IdealDurationSeconds = reader.IsDBNull(18) ? null : reader.GetDecimal(18),
            ConflictingStandardVersionIds = conflicts,
        };
        return new PublishedProductionReferenceTimeOutcome(
            new ProductionReferenceTimeAuthorityRevision(checked((long)reader.GetDecimal(0))), resolution);
    }

    private static bool Equivalent(ProductionReferenceTimeResolution first, ProductionReferenceTimeResolution second) =>
        first with { ConflictingStandardVersionIds = second.ConflictingStandardVersionIds } == second &&
        first.ConflictingStandardVersionIds.SequenceEqual(second.ConflictingStandardVersionIds, StringComparer.Ordinal);

    private static async Task InsertOutcomeAsync(
        SqlConnection connection, SqlTransaction transaction, long processorRowId,
        PublishedProductionReferenceTimeOutcome published, CancellationToken cancellationToken)
    {
        var value = published.Resolution;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO dbo.ProductionReferenceTimeOutcome
              (MetricAggregationProcessorRowId, ProductionReferenceTimeRevision, SourceQuantityEvidenceId,
               CompanyId, SiteId, MachineId, ShiftOccurrenceSiteId, ShiftScheduleAssignmentId,
               ShiftId, ShiftStartsAtUtc, ShiftEndsAtUtc, ProductionDaySiteId, ProductionBusinessDate,
               OperationId, PartId, OccurredAtUtc, ProducedQuantity, ProductionStandardAuthorityRevision,
               ResolutionStatus, SelectedStandardVersionId, SelectedStandardSourceReference,
               IdealProductionDurationSeconds)
            VALUES
              (@Processor, @Revision, @Source, @Company, @Site, @Machine, @Site, @Schedule,
               @Shift, @Start, @End, @Site, @Day, @Operation, @Part, @Occurred, @Units,
               @Authority, @Status, @Selected, @Reference, @Seconds);
            """;
        command.Parameters.Add("@Processor", SqlDbType.BigInt).Value = processorRowId;
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@Revision", checked((ulong)published.PublicationRevision.Value)));
        command.Parameters.Add("@Source", SqlDbType.NVarChar, 256).Value = value.SourceQuantityEvidenceId.Value;
        command.Parameters.Add("@Company", SqlDbType.NVarChar, 256).Value = value.CompanyId.Value;
        command.Parameters.Add("@Site", SqlDbType.NVarChar, 256).Value = value.SiteId.Value;
        command.Parameters.Add("@Machine", SqlDbType.UniqueIdentifier).Value = value.MachineId.Value;
        command.Parameters.Add("@Schedule", SqlDbType.NVarChar, 256).Value = value.ShiftOccurrenceId.ShiftScheduleAssignmentId.Value;
        command.Parameters.Add("@Shift", SqlDbType.NVarChar, 256).Value = value.ShiftOccurrenceId.ShiftId.Value;
        command.Parameters.Add("@Start", SqlDbType.DateTimeOffset).Value = value.ShiftOccurrenceId.StartsAtUtc;
        command.Parameters.Add("@End", SqlDbType.DateTimeOffset).Value = value.ShiftOccurrenceId.EndsAtUtc;
        command.Parameters.Add("@Day", SqlDbType.Date).Value = value.ProductionDayId.BusinessDate.ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add("@Operation", SqlDbType.NVarChar, 256).Value = (object?)value.OperationId?.Value ?? DBNull.Value;
        command.Parameters.Add("@Part", SqlDbType.NVarChar, 256).Value = (object?)value.PartId?.Value ?? DBNull.Value;
        command.Parameters.Add("@Occurred", SqlDbType.DateTimeOffset).Value = value.OccurredAtUtc;
        command.Parameters.Add("@Units", SqlDbType.Int).Value = value.ProducedUnits;
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@Authority", checked((ulong)value.AuthorityRevision)));
        command.Parameters.Add("@Status", SqlDbType.TinyInt).Value = (byte)value.Status;
        command.Parameters.Add("@Selected", SqlDbType.NVarChar, 256).Value = (object?)value.SelectedStandardVersionId ?? DBNull.Value;
        command.Parameters.Add("@Reference", SqlDbType.NVarChar, 1024).Value = (object?)value.SelectedStandardSourceReference ?? DBNull.Value;
        var seconds = command.Parameters.Add("@Seconds", SqlDbType.Decimal);
        seconds.Precision = 20;
        seconds.Scale = 6;
        seconds.Value = (object?)value.IdealDurationSeconds ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);

        foreach (var conflict in value.ConflictingStandardVersionIds)
        {
            await using var item = connection.CreateCommand();
            item.Transaction = transaction;
            item.CommandText = """
                INSERT INTO dbo.ProductionReferenceTimeOutcomeConflict
                    (MetricAggregationProcessorRowId, ProductionReferenceTimeRevision,
                     SourceQuantityEvidenceId, ConflictingStandardVersionId)
                VALUES (@Processor, @Revision, @Source, @Conflict);
                """;
            item.Parameters.Add("@Processor", SqlDbType.BigInt).Value = processorRowId;
            item.Parameters.Add(SqlServerUInt64.CreateParameter("@Revision", checked((ulong)published.PublicationRevision.Value)));
            item.Parameters.Add("@Source", SqlDbType.NVarChar, 256).Value = value.SourceQuantityEvidenceId.Value;
            item.Parameters.Add("@Conflict", SqlDbType.NVarChar, 256).Value = conflict;
            await item.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
