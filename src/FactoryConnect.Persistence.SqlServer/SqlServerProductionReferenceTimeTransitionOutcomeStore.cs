using System.Data;
using System.Text.Json;
using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>
/// Internal claim-bound D4 admission seam. The transition row is locked first;
/// exact delta membership, one-S enforcement, replay, R progression, outcome
/// persistence, and the legacy (A,R) observation pair are committed together.
/// </summary>
internal sealed class SqlServerProductionReferenceTimeTransitionOutcomeStore
{
    private readonly string _connectionString;

    public SqlServerProductionReferenceTimeTransitionOutcomeStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async Task<PublishedProductionReferenceTimeOutcome> AdmitAsync(
        MetricAggregationProcessorId processorId,
        MetricInputPosition targetAggregationPosition,
        PublishedProductionReferenceTimeOutcome proposed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(targetAggregationPosition);
        ArgumentNullException.ThrowIfNull(proposed);
        proposed.Resolution.Validate();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var processorRowId = await SqlServerProductionReferenceTimePublicationTransitionStore.ReadProcessorRowIdAsync(
            connection,
            transaction,
            processorId,
            cancellationToken);
        var claim = await ReadPendingClaimAsync(
            connection,
            transaction,
            processorId,
            processorRowId,
            targetAggregationPosition,
            cancellationToken);

        if (claim.ProductionStandardAuthorityRevision is not long standardRevision)
        {
            throw new InvalidOperationException(
                "An empty reference-time publication transition cannot admit source outcomes.");
        }

        if (proposed.Resolution.AuthorityRevision != standardRevision)
        {
            throw new InvalidOperationException(
                "A pending reference-time publication transition cannot admit an outcome from a different production-standard authority revision.");
        }

        var expectedSourceIds = await SqlServerProductionReferenceTimePublicationTransitionStore.ReadExpectedSourceIdsAsync(
            connection,
            transaction,
            processorRowId,
            claim.ExpectedPreviousAggregationPosition,
            targetAggregationPosition,
            cancellationToken);
        if (!expectedSourceIds.Contains(proposed.SourceQuantityEvidenceId.Value))
        {
            throw new InvalidOperationException(
                "Reference-time source outcome does not belong to the exact claimed aggregation delta.");
        }

        var existing = await ReadSourceAsync(
            connection,
            transaction,
            processorRowId,
            proposed.SourceQuantityEvidenceId,
            cancellationToken);
        if (existing is not null)
        {
            if (existing.PublicationRevision.Value <= claim.StartingReferenceTimeRevision.Value)
            {
                throw new InvalidOperationException(
                    "Reference-time source was already covered before the current transition began.");
            }

            if (!Equivalent(existing.Resolution, proposed.Resolution))
            {
                throw new InvalidOperationException(
                    "Ordinary replay cannot change an admitted reference-time source outcome.");
            }

            await transaction.CommitAsync(cancellationToken);
            return existing;
        }

        var latestRevision = await SqlServerProductionReferenceTimePublicationTransitionStore.ReadLatestReferenceTimeRevisionAsync(
            connection,
            transaction,
            processorRowId,
            cancellationToken);
        if (proposed.PublicationRevision.Value != checked(latestRevision.Value + 1))
        {
            throw new ReferenceTimeRevisionRaceException();
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
                "@Revision",
                checked((ulong)proposed.PublicationRevision.Value)));
            await revision.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertOutcomeAsync(
            connection,
            transaction,
            processorRowId,
            proposed,
            cancellationToken);

        await using (var cut = connection.CreateCommand())
        {
            cut.Transaction = transaction;
            cut.CommandText = """
                INSERT INTO dbo.ProductionReferenceTimePublicationCut
                    (MetricAggregationProcessorRowId,
                     MetricAggregationPosition,
                     ProductionReferenceTimeRevision)
                VALUES (@Processor, @Target, @Revision);
                """;
            cut.Parameters.Add("@Processor", SqlDbType.BigInt).Value = processorRowId;
            cut.Parameters.Add(SqlServerUInt64.CreateParameter("@Target", targetAggregationPosition.Value));
            cut.Parameters.Add(SqlServerUInt64.CreateParameter(
                "@Revision",
                checked((ulong)proposed.PublicationRevision.Value)));
            await cut.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return proposed;
    }

    private static async Task<ProductionReferenceTimePublicationTransition> ReadPendingClaimAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        MetricAggregationProcessorId processorId,
        long processorRowId,
        MetricInputPosition target,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT ExpectedPreviousMetricAggregationPosition,
                   ProductionStandardAuthorityRevision,
                   StartingProductionReferenceTimeRevision,
                   State,
                   FinalProductionReferenceTimeRevision
            FROM dbo.ProductionReferenceTimePublicationTransition WITH (UPDLOCK, HOLDLOCK)
            WHERE MetricAggregationProcessorRowId = @Processor
              AND TargetMetricAggregationPosition = @Target;
            """;
        command.Parameters.Add("@Processor", SqlDbType.BigInt).Value = processorRowId;
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@Target", target.Value));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "Reference-time publication transition is not available for source admission.");
        }

        MetricInputPosition? previous = reader.IsDBNull(0)
            ? null
            : new MetricInputPosition(checked((ulong)reader.GetDecimal(0)));
        long? standard = reader.IsDBNull(1)
            ? null
            : checked((long)reader.GetDecimal(1));
        var starting = new ProductionReferenceTimeAuthorityRevision(checked((long)reader.GetDecimal(2)));
        var state = reader.GetByte(3);
        ProductionReferenceTimeAuthorityRevision? final = reader.IsDBNull(4)
            ? null
            : new ProductionReferenceTimeAuthorityRevision(checked((long)reader.GetDecimal(4)));
        if (state != 0)
        {
            throw new ReferenceTimeTransitionCompletedException();
        }

        return new ProductionReferenceTimePublicationTransition(
            processorId,
            target,
            previous,
            standard,
            starting,
            final,
            false);
    }

    private static async Task<PublishedProductionReferenceTimeOutcome?> ReadSourceAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long processorRowId,
        ProductionQuantityEvidenceId sourceId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = OutcomeSelect +
            " WHERE o.MetricAggregationProcessorRowId = @Processor AND o.SourceQuantityEvidenceId = @Source;";
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
        var conflicts = reader.IsDBNull(19)
            ? []
            : JsonDocument.Parse(reader.GetString(19)).RootElement.EnumerateArray()
                .Select(static item => item.GetProperty("ConflictingStandardVersionId").GetString()!)
                .ToArray();
        var site = new SiteId(reader.GetString(3));
        var shift = new ShiftOccurrenceId(
            site,
            new ShiftScheduleAssignmentId(reader.GetString(5)),
            new ShiftId(reader.GetString(6)),
            reader.GetDateTimeOffset(7),
            reader.GetDateTimeOffset(8));
        var resolution = new ProductionReferenceTimeResolution
        {
            SourceQuantityEvidenceId = new ProductionQuantityEvidenceId(reader.GetString(1)),
            CompanyId = new CompanyId(reader.GetString(2)),
            SiteId = site,
            MachineId = new MachineId(reader.GetGuid(4)),
            ShiftOccurrenceId = shift,
            ProductionDayId = new ProductionDayId(
                site,
                DateOnly.FromDateTime(reader.GetDateTime(9))),
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
            new ProductionReferenceTimeAuthorityRevision(checked((long)reader.GetDecimal(0))),
            resolution);
    }

    private static bool Equivalent(
        ProductionReferenceTimeResolution first,
        ProductionReferenceTimeResolution second) =>
        first with { ConflictingStandardVersionIds = second.ConflictingStandardVersionIds } == second &&
        first.ConflictingStandardVersionIds.SequenceEqual(
            second.ConflictingStandardVersionIds,
            StringComparer.Ordinal);

    private static async Task InsertOutcomeAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long processorRowId,
        PublishedProductionReferenceTimeOutcome published,
        CancellationToken cancellationToken)
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
        command.Parameters.Add(SqlServerUInt64.CreateParameter(
            "@Revision", checked((ulong)published.PublicationRevision.Value)));
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
        command.Parameters.Add(SqlServerUInt64.CreateParameter(
            "@Authority", checked((ulong)value.AuthorityRevision)));
        command.Parameters.Add("@Status", SqlDbType.TinyInt).Value = (byte)value.Status;
        command.Parameters.Add("@Selected", SqlDbType.NVarChar, 256).Value =
            (object?)value.SelectedStandardVersionId ?? DBNull.Value;
        command.Parameters.Add("@Reference", SqlDbType.NVarChar, 1024).Value =
            (object?)value.SelectedStandardSourceReference ?? DBNull.Value;
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
            item.Parameters.Add(SqlServerUInt64.CreateParameter(
                "@Revision", checked((ulong)published.PublicationRevision.Value)));
            item.Parameters.Add("@Source", SqlDbType.NVarChar, 256).Value = value.SourceQuantityEvidenceId.Value;
            item.Parameters.Add("@Conflict", SqlDbType.NVarChar, 256).Value = conflict;
            await item.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
