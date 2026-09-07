using System.Data;
using System.Globalization;
using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal enum SqlServerOperationalMetricProjectionPublicationStage
{
    EvidenceDeleted,
    ManifestDeleted,
    ObsoleteProjectionDeleted,
    RetainedProjectionUpdated,
    NewProjectionInserted,
    ManifestInserted,
    EvidenceInserted,
    ExactStateRevalidated,
}

internal sealed record SqlServerOperationalMetricProjectionPublishedRow(
    long ProjectionRowId,
    SqlServerOperationalMetricProjectionPreparedRow PreparedRow);

internal sealed record SqlServerOperationalMetricProjectionPublicationResult(
    IReadOnlyList<SqlServerOperationalMetricProjectionPublishedRow> PublishedRows);

/// <summary>
/// Owns the FC-030.2C.4 publication transition after the complete frozen
/// processor -> checkpoint -> projection -> manifest -> evidence lock boundary.
/// </summary>
internal static class SqlServerOperationalMetricProjectionPublication
{
    internal static Task<SqlServerOperationalMetricProjectionPublicationResult> ExecuteAsync(
        SqlServerOperationalMetricProjectionCommitContext context,
        OperationalMetricProjectionCommit commit,
        CancellationToken cancellationToken) =>
        ExecuteAsync(context, commit, failureInjection: null, cancellationToken);

    internal static async Task<SqlServerOperationalMetricProjectionPublicationResult> ExecuteAsync(
        SqlServerOperationalMetricProjectionCommitContext context,
        OperationalMetricProjectionCommit commit,
        Func<SqlServerOperationalMetricProjectionPublicationStage, CancellationToken, Task>? failureInjection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(commit);

        // This composite call is the sole DML authorization gate. No publication
        // mutation may occur before all projection, manifest, and evidence locks
        // have been acquired and validated in the frozen order in this transaction.
        var preparation = await SqlServerOperationalMetricProjectionPublicationLocks.PrepareAsync(
            context,
            commit,
            cancellationToken).ConfigureAwait(false);

        if (context.Mode == SqlServerOperationalMetricProjectionCommitMode.ReconcileProposed)
        {
            var replayRows = ResolveExistingPublishedRows(preparation.ProjectionPlan);
            await ValidateExactPublishedStateAsync(
                context,
                replayRows,
                cancellationToken).ConfigureAwait(false);
            return new SqlServerOperationalMetricProjectionPublicationResult(replayRows);
        }

        if (context.Mode != SqlServerOperationalMetricProjectionCommitMode.Advance)
        {
            throw new InvalidOperationException("Unsupported operational metric projection commit mode.");
        }

        var currentRowIds = preparation.Locks.ManifestProjectionRowIds
            .OrderBy(static rowId => rowId)
            .ToArray();

        await DeleteCurrentEvidenceAsync(context, currentRowIds, cancellationToken).ConfigureAwait(false);
        await InjectAsync(failureInjection, SqlServerOperationalMetricProjectionPublicationStage.EvidenceDeleted, cancellationToken)
            .ConfigureAwait(false);

        await DeleteObsoleteManifestMembershipAsync(
            context,
            preparation.ProjectionPlan.ObsoleteProjectionRowIds,
            cancellationToken).ConfigureAwait(false);
        await InjectAsync(failureInjection, SqlServerOperationalMetricProjectionPublicationStage.ManifestDeleted, cancellationToken)
            .ConfigureAwait(false);

        await DeleteObsoleteProjectionRowsAsync(
            context,
            preparation.ProjectionPlan.ObsoleteProjectionRowIds,
            cancellationToken).ConfigureAwait(false);
        await InjectAsync(failureInjection, SqlServerOperationalMetricProjectionPublicationStage.ObsoleteProjectionDeleted, cancellationToken)
            .ConfigureAwait(false);

        var published = new List<SqlServerOperationalMetricProjectionPublishedRow>(
            preparation.ProjectionPlan.ProposedRows.Count);

        foreach (var row in preparation.ProjectionPlan.ProposedRows
                     .Where(static row => row.ExistingProjectionRowId.HasValue)
                     .OrderBy(static row => row.ExistingProjectionRowId!.Value))
        {
            var rowId = row.ExistingProjectionRowId!.Value;
            await UpdateRetainedProjectionAsync(context, rowId, row.WriteModel, cancellationToken)
                .ConfigureAwait(false);
            published.Add(new SqlServerOperationalMetricProjectionPublishedRow(rowId, row));
        }
        await InjectAsync(failureInjection, SqlServerOperationalMetricProjectionPublicationStage.RetainedProjectionUpdated, cancellationToken)
            .ConfigureAwait(false);

        foreach (var row in preparation.ProjectionPlan.ProposedRows
                     .Where(static row => !row.ExistingProjectionRowId.HasValue)
                     .OrderBy(static row => row.EvaluationKeyHash, ByteArrayComparer.Instance))
        {
            var rowId = await InsertProjectionAsync(context, row.WriteModel, cancellationToken)
                .ConfigureAwait(false);
            published.Add(new SqlServerOperationalMetricProjectionPublishedRow(rowId, row));
        }
        await InjectAsync(failureInjection, SqlServerOperationalMetricProjectionPublicationStage.NewProjectionInserted, cancellationToken)
            .ConfigureAwait(false);

        published.Sort(static (left, right) => left.ProjectionRowId.CompareTo(right.ProjectionRowId));

        var newRows = published
            .Where(static row => !row.PreparedRow.ExistingProjectionRowId.HasValue)
            .OrderBy(static row => row.ProjectionRowId)
            .ToArray();
        await InsertNewManifestMembershipAsync(context, newRows, cancellationToken).ConfigureAwait(false);
        await InjectAsync(failureInjection, SqlServerOperationalMetricProjectionPublicationStage.ManifestInserted, cancellationToken)
            .ConfigureAwait(false);

        await InsertCompleteEvidenceAsync(context, published, cancellationToken).ConfigureAwait(false);
        await InjectAsync(failureInjection, SqlServerOperationalMetricProjectionPublicationStage.EvidenceInserted, cancellationToken)
            .ConfigureAwait(false);

        await ValidateExactPublishedStateAsync(context, published, cancellationToken).ConfigureAwait(false);
        await InjectAsync(failureInjection, SqlServerOperationalMetricProjectionPublicationStage.ExactStateRevalidated, cancellationToken)
            .ConfigureAwait(false);

        return new SqlServerOperationalMetricProjectionPublicationResult(published.ToArray());
    }

    private static IReadOnlyList<SqlServerOperationalMetricProjectionPublishedRow> ResolveExistingPublishedRows(
        SqlServerOperationalMetricProjectionMutationPlan plan)
    {
        if (plan.ObsoleteProjectionRowIds.Count != 0 ||
            plan.ProposedRows.Any(static row => !row.ExistingProjectionRowId.HasValue))
        {
            throw new InvalidOperationException(
                "Operational metric projection replay does not exactly match the durable projection identity set.");
        }

        return plan.ProposedRows
            .Select(static row => new SqlServerOperationalMetricProjectionPublishedRow(
                row.ExistingProjectionRowId!.Value,
                row))
            .OrderBy(static row => row.ProjectionRowId)
            .ToArray();
    }

    private static async Task DeleteCurrentEvidenceAsync(
        SqlServerOperationalMetricProjectionCommitContext context,
        IReadOnlyList<long> projectionRowIds,
        CancellationToken cancellationToken)
    {
        foreach (var rowId in projectionRowIds)
        {
            await using var command = context.Connection.CreateCommand();
            command.Transaction = context.Transaction;
            command.CommandText =
                "DELETE FROM dbo.OperationalMetricProjectionEvidence " +
                "WHERE OperationalMetricProjectionRowId = @ProjectionRowId;";
            command.Parameters.Add("@ProjectionRowId", SqlDbType.BigInt).Value = rowId;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task DeleteObsoleteManifestMembershipAsync(
        SqlServerOperationalMetricProjectionCommitContext context,
        IReadOnlyList<long> obsoleteProjectionRowIds,
        CancellationToken cancellationToken)
    {
        foreach (var rowId in obsoleteProjectionRowIds.OrderBy(static value => value))
        {
            await using var command = context.Connection.CreateCommand();
            command.Transaction = context.Transaction;
            command.CommandText =
                "DELETE FROM dbo.OperationalMetricProjectionManifest " +
                "WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId " +
                "AND OperationalMetricProjectionRowId = @ProjectionRowId;";
            command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = context.ProjectionProcessorRowId;
            command.Parameters.Add("@ProjectionRowId", SqlDbType.BigInt).Value = rowId;
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    "Operational metric projection manifest membership disappeared before dependency-safe deletion.");
            }
        }
    }

    private static async Task DeleteObsoleteProjectionRowsAsync(
        SqlServerOperationalMetricProjectionCommitContext context,
        IReadOnlyList<long> obsoleteProjectionRowIds,
        CancellationToken cancellationToken)
    {
        foreach (var rowId in obsoleteProjectionRowIds.OrderBy(static value => value))
        {
            await using var command = context.Connection.CreateCommand();
            command.Transaction = context.Transaction;
            command.CommandText =
                "DELETE FROM dbo.OperationalMetricProjection " +
                "WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId " +
                "AND OperationalMetricProjectionRowId = @ProjectionRowId;";
            command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = context.ProjectionProcessorRowId;
            command.Parameters.Add("@ProjectionRowId", SqlDbType.BigInt).Value = rowId;
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    "Operational metric projection row disappeared before dependency-safe deletion.");
            }
        }
    }

    private static async Task UpdateRetainedProjectionAsync(
        SqlServerOperationalMetricProjectionCommitContext context,
        long projectionRowId,
        SqlServerOperationalMetricProjectionWriteModel model,
        CancellationToken cancellationToken)
    {
        await using var command = context.Connection.CreateCommand();
        command.Transaction = context.Transaction;
        command.CommandText = """
            UPDATE dbo.OperationalMetricProjection
            SET Status = @Status,
                MetricValue = @MetricValue,
                Unit = @Unit,
                ReasonCode = @ReasonCode,
                ReasonOperandName = @ReasonOperandName,
                SourceRevisionPosition = @SourceRevisionPosition
            WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId
              AND OperationalMetricProjectionRowId = @ProjectionRowId;
            """;
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = context.ProjectionProcessorRowId;
        command.Parameters.Add("@ProjectionRowId", SqlDbType.BigInt).Value = projectionRowId;
        AddProjectionStateParameters(command, model);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("Retained operational metric projection row disappeared before update.");
        }
    }

    private static async Task<long> InsertProjectionAsync(
        SqlServerOperationalMetricProjectionCommitContext context,
        SqlServerOperationalMetricProjectionWriteModel model,
        CancellationToken cancellationToken)
    {
        await using var command = context.Connection.CreateCommand();
        command.Transaction = context.Transaction;
        command.CommandText = """
            INSERT INTO dbo.OperationalMetricProjection
            (
                OperationalMetricProjectionProcessorRowId,
                EvaluationKeyCodecVersion,
                EvaluationKeyHash,
                EvaluationKeyBinary,
                MachineId,
                PeriodKind,
                PeriodSiteId,
                PeriodSiteOrderKey,
                ShiftScheduleAssignmentId,
                ShiftScheduleAssignmentOrderKey,
                ShiftId,
                ShiftOrderKey,
                ShiftStartsAtUtc,
                ShiftEndsAtUtc,
                ProductionBusinessDate,
                ProductionOrderPresent,
                ProductionOrderId,
                ProductionOrderOrderKey,
                OperationPresent,
                OperationId,
                OperationOrderKey,
                PartPresent,
                PartId,
                PartOrderKey,
                OperatorPresent,
                OperatorId,
                OperatorOrderKey,
                MetricKey,
                MetricKeyOrderKey,
                DefinitionVersion,
                DefinitionVersionOrderKey,
                Status,
                MetricValue,
                Unit,
                ReasonCode,
                ReasonOperandName,
                SourceRevisionPosition
            )
            OUTPUT INSERTED.OperationalMetricProjectionRowId
            VALUES
            (
                @ProcessorRowId,
                @EvaluationKeyCodecVersion,
                @EvaluationKeyHash,
                @EvaluationKeyBinary,
                @MachineId,
                @PeriodKind,
                @PeriodSiteId,
                @PeriodSiteOrderKey,
                @ShiftScheduleAssignmentId,
                @ShiftScheduleAssignmentOrderKey,
                @ShiftId,
                @ShiftOrderKey,
                @ShiftStartsAtUtc,
                @ShiftEndsAtUtc,
                @ProductionBusinessDate,
                @ProductionOrderPresent,
                @ProductionOrderId,
                @ProductionOrderOrderKey,
                @OperationPresent,
                @OperationId,
                @OperationOrderKey,
                @PartPresent,
                @PartId,
                @PartOrderKey,
                @OperatorPresent,
                @OperatorId,
                @OperatorOrderKey,
                @MetricKey,
                @MetricKeyOrderKey,
                @DefinitionVersion,
                @DefinitionVersionOrderKey,
                @Status,
                @MetricValue,
                @Unit,
                @ReasonCode,
                @ReasonOperandName,
                @SourceRevisionPosition
            );
            """;
        AddProjectionParameters(command, context.ProjectionProcessorRowId, model);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static async Task InsertNewManifestMembershipAsync(
        SqlServerOperationalMetricProjectionCommitContext context,
        IReadOnlyList<SqlServerOperationalMetricProjectionPublishedRow> newRows,
        CancellationToken cancellationToken)
    {
        foreach (var row in newRows)
        {
            await using var command = context.Connection.CreateCommand();
            command.Transaction = context.Transaction;
            command.CommandText = """
                INSERT INTO dbo.OperationalMetricProjectionManifest
                    (OperationalMetricProjectionProcessorRowId, OperationalMetricProjectionRowId)
                VALUES (@ProcessorRowId, @ProjectionRowId);
                """;
            command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = context.ProjectionProcessorRowId;
            command.Parameters.Add("@ProjectionRowId", SqlDbType.BigInt).Value = row.ProjectionRowId;
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("Operational metric projection manifest insertion failed.");
            }
        }
    }

    private static async Task InsertCompleteEvidenceAsync(
        SqlServerOperationalMetricProjectionCommitContext context,
        IReadOnlyList<SqlServerOperationalMetricProjectionPublishedRow> publishedRows,
        CancellationToken cancellationToken)
    {
        foreach (var published in publishedRows.OrderBy(static row => row.ProjectionRowId))
        {
            var projection = published.PreparedRow.Projection;

            for (var ordinal = 0; ordinal < projection.OperandEvidence.Count; ordinal++)
            {
                await InsertComponentEvidenceAsync(
                    context,
                    published.ProjectionRowId,
                    ordinal,
                    projection.OperandEvidence[ordinal],
                    cancellationToken).ConfigureAwait(false);
            }

            for (var ordinal = 0; ordinal < projection.DependencyEvidence.Count; ordinal++)
            {
                await InsertDependencyEvidenceAsync(
                    context,
                    published.ProjectionRowId,
                    ordinal,
                    projection.DependencyEvidence[ordinal],
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task InsertComponentEvidenceAsync(
        SqlServerOperationalMetricProjectionCommitContext context,
        long projectionRowId,
        int ordinal,
        OperationalMetricComponentProjectionEvidence evidence,
        CancellationToken cancellationToken)
    {
        await using var command = context.Connection.CreateCommand();
        command.Transaction = context.Transaction;
        command.CommandText = """
            INSERT INTO dbo.OperationalMetricProjectionEvidence
            (
                OperationalMetricProjectionRowId,
                EvidenceKind,
                EvidenceOrdinal,
                OperandName,
                OperandNameOrderKey,
                ComponentKey,
                MetricDimension,
                ComponentValue,
                ComponentUnit,
                InputCount,
                FirstInputTimestamp,
                LastInputTimestamp
            )
            VALUES
            (
                @ProjectionRowId,
                1,
                @EvidenceOrdinal,
                @OperandName,
                @OperandNameOrderKey,
                @ComponentKey,
                @MetricDimension,
                @ComponentValue,
                @ComponentUnit,
                @InputCount,
                @FirstInputTimestamp,
                @LastInputTimestamp
            );
            """;
        command.Parameters.Add("@ProjectionRowId", SqlDbType.BigInt).Value = projectionRowId;
        command.Parameters.Add("@EvidenceOrdinal", SqlDbType.Int).Value = ordinal;
        AddString(command, "@OperandName", 256, evidence.OperandName);
        command.Parameters.Add("@OperandNameOrderKey", SqlDbType.VarBinary, StringOrderKeyV2Codec.MaximumEncodedLength)
            .Value = StringOrderKeyV2Codec.Encode(evidence.OperandName);
        AddString(command, "@ComponentKey", 256, evidence.SourceIdentity.ComponentKey);
        command.Parameters.Add("@MetricDimension", SqlDbType.TinyInt).Value = checked((byte)evidence.Dimension);
        AddString(command, "@ComponentValue", 64, CanonicalDecimalTextV1Codec.Serialize(evidence.Value));
        AddString(command, "@ComponentUnit", 128, evidence.Unit);
        command.Parameters.Add("@InputCount", SqlDbType.Decimal).Value = Convert.ToDecimal(evidence.InputCount, CultureInfo.InvariantCulture);
        command.Parameters.Add("@FirstInputTimestamp", SqlDbType.DateTimeOffset).Value = evidence.FirstInputTimestamp;
        command.Parameters.Add("@LastInputTimestamp", SqlDbType.DateTimeOffset).Value = evidence.LastInputTimestamp;
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("Operational metric component evidence insertion failed.");
        }
    }

    private static async Task InsertDependencyEvidenceAsync(
        SqlServerOperationalMetricProjectionCommitContext context,
        long projectionRowId,
        int ordinal,
        OperationalMetricDependencyProjectionEvidence evidence,
        CancellationToken cancellationToken)
    {
        var snapshot = OperationalMetricDependencySnapshotV1Codec.Encode(evidence.Projection);
        var hash = OperationalMetricDependencySnapshotV1Codec.ComputeHash(snapshot);

        await using var command = context.Connection.CreateCommand();
        command.Transaction = context.Transaction;
        command.CommandText = """
            INSERT INTO dbo.OperationalMetricProjectionEvidence
            (
                OperationalMetricProjectionRowId,
                EvidenceKind,
                EvidenceOrdinal,
                OperandName,
                OperandNameOrderKey,
                DependencyMetricKey,
                DependencyDefinitionVersion,
                DependencySnapshotCodecVersion,
                DependencySnapshotHash,
                DependencySnapshotBinary
            )
            VALUES
            (
                @ProjectionRowId,
                2,
                @EvidenceOrdinal,
                @OperandName,
                @OperandNameOrderKey,
                @MetricKey,
                @DefinitionVersion,
                @SnapshotCodecVersion,
                @SnapshotHash,
                @SnapshotBinary
            );
            """;
        command.Parameters.Add("@ProjectionRowId", SqlDbType.BigInt).Value = projectionRowId;
        command.Parameters.Add("@EvidenceOrdinal", SqlDbType.Int).Value = ordinal;
        AddString(command, "@OperandName", 256, evidence.OperandName);
        command.Parameters.Add("@OperandNameOrderKey", SqlDbType.VarBinary, StringOrderKeyV2Codec.MaximumEncodedLength)
            .Value = StringOrderKeyV2Codec.Encode(evidence.OperandName);
        AddString(command, "@MetricKey", 256, evidence.DefinitionId.MetricKey);
        AddString(command, "@DefinitionVersion", 256, evidence.DefinitionId.Version);
        command.Parameters.Add("@SnapshotCodecVersion", SqlDbType.SmallInt).Value =
            OperationalMetricDependencySnapshotV1Codec.CodecVersion;
        command.Parameters.Add("@SnapshotHash", SqlDbType.Binary, 32).Value = hash;
        command.Parameters.Add("@SnapshotBinary", SqlDbType.VarBinary, -1).Value = snapshot;
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("Operational metric dependency evidence insertion failed.");
        }
    }

    private static async Task ValidateExactPublishedStateAsync(
        SqlServerOperationalMetricProjectionCommitContext context,
        IReadOnlyList<SqlServerOperationalMetricProjectionPublishedRow> expectedRows,
        CancellationToken cancellationToken)
    {
        var expectedById = expectedRows.ToDictionary(static row => row.ProjectionRowId);
        var observedIds = new List<long>();

        await using (var command = context.Connection.CreateCommand())
        {
            command.Transaction = context.Transaction;
            command.CommandText = """
                SELECT
                    OperationalMetricProjectionRowId,
                    EvaluationKeyCodecVersion,
                    EvaluationKeyHash,
                    EvaluationKeyBinary,
                    Status,
                    MetricValue,
                    Unit,
                    ReasonCode,
                    ReasonOperandName,
                    SourceRevisionPosition
                FROM dbo.OperationalMetricProjection
                WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId
                ORDER BY OperationalMetricProjectionRowId;
                """;
            command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = context.ProjectionProcessorRowId;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var rowId = reader.GetInt64(0);
                observedIds.Add(rowId);
                if (!expectedById.TryGetValue(rowId, out var expected))
                {
                    throw new InvalidOperationException("Operational metric projection publication contains an unexpected durable row.");
                }

                var model = expected.PreparedRow.WriteModel;
                var projection = model.Projection;
                if (reader.GetInt16(1) != OperationalMetricEvaluationKeyV1Codec.CodecVersion ||
                    !((byte[])reader[2]).AsSpan().SequenceEqual(model.EvaluationKeyHash) ||
                    !((byte[])reader[3]).AsSpan().SequenceEqual(model.EvaluationKeyBinary) ||
                    reader.GetByte(4) != checked((byte)projection.Status) ||
                    !NullableStringEquals(reader, 5, model.MetricValue) ||
                    !string.Equals(reader.GetString(6), projection.Unit, StringComparison.Ordinal) ||
                    !NullableByteEquals(reader, 7, projection.ReasonCode is null ? null : checked((byte)projection.ReasonCode.Value)) ||
                    !NullableStringEquals(reader, 8, projection.ReasonOperandName) ||
                    SqlServerUInt64.Materialize(reader.GetDecimal(9)) != projection.SourceRevision.Position.Value)
                {
                    throw new InvalidOperationException("Operational metric projection durable state does not exactly match the proposed publication.");
                }
            }
        }

        if (!observedIds.SequenceEqual(expectedRows.Select(static row => row.ProjectionRowId).Order()))
        {
            throw new InvalidOperationException("Operational metric projection durable row set does not exactly match the proposed publication.");
        }

        var manifestIds = new List<long>();
        await using (var command = context.Connection.CreateCommand())
        {
            command.Transaction = context.Transaction;
            command.CommandText = """
                SELECT OperationalMetricProjectionRowId
                FROM dbo.OperationalMetricProjectionManifest
                WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId
                ORDER BY OperationalMetricProjectionRowId;
                """;
            command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = context.ProjectionProcessorRowId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                manifestIds.Add(reader.GetInt64(0));
            }
        }

        if (!manifestIds.SequenceEqual(expectedRows.Select(static row => row.ProjectionRowId).Order()))
        {
            throw new InvalidOperationException("Operational metric projection manifest does not exactly match the proposed publication.");
        }

        var expectedEvidence = BuildExpectedEvidence(expectedRows);
        var observedEvidence = await ReadEvidenceAsync(context, cancellationToken).ConfigureAwait(false);
        if (observedEvidence.Count != expectedEvidence.Count)
        {
            throw new InvalidOperationException("Operational metric projection evidence count does not exactly match the proposed publication.");
        }

        for (var index = 0; index < expectedEvidence.Count; index++)
        {
            if (!expectedEvidence[index].ExactEquals(observedEvidence[index]))
            {
                throw new InvalidOperationException("Operational metric projection evidence does not exactly match the proposed publication.");
            }
        }
    }

    private static IReadOnlyList<ExpectedEvidenceRow> BuildExpectedEvidence(
        IReadOnlyList<SqlServerOperationalMetricProjectionPublishedRow> publishedRows)
    {
        var result = new List<ExpectedEvidenceRow>();
        foreach (var published in publishedRows.OrderBy(static row => row.ProjectionRowId))
        {
            var projection = published.PreparedRow.Projection;
            for (var ordinal = 0; ordinal < projection.OperandEvidence.Count; ordinal++)
            {
                var evidence = projection.OperandEvidence[ordinal];
                result.Add(ExpectedEvidenceRow.ForComponent(published.ProjectionRowId, ordinal, evidence));
            }

            for (var ordinal = 0; ordinal < projection.DependencyEvidence.Count; ordinal++)
            {
                var evidence = projection.DependencyEvidence[ordinal];
                result.Add(ExpectedEvidenceRow.ForDependency(published.ProjectionRowId, ordinal, evidence));
            }
        }

        return result
            .OrderBy(static row => row.ProjectionRowId)
            .ThenBy(static row => row.EvidenceKind)
            .ThenBy(static row => row.EvidenceOrdinal)
            .ToArray();
    }

    private static async Task<IReadOnlyList<ObservedEvidenceRow>> ReadEvidenceAsync(
        SqlServerOperationalMetricProjectionCommitContext context,
        CancellationToken cancellationToken)
    {
        await using var command = context.Connection.CreateCommand();
        command.Transaction = context.Transaction;
        command.CommandText = """
            SELECT
                e.OperationalMetricProjectionRowId,
                e.EvidenceKind,
                e.EvidenceOrdinal,
                e.OperandName,
                e.OperandNameOrderKey,
                e.ComponentKey,
                e.MetricDimension,
                e.ComponentValue,
                e.ComponentUnit,
                e.InputCount,
                e.FirstInputTimestamp,
                e.LastInputTimestamp,
                e.DependencyMetricKey,
                e.DependencyDefinitionVersion,
                e.DependencySnapshotCodecVersion,
                e.DependencySnapshotHash,
                e.DependencySnapshotBinary
            FROM dbo.OperationalMetricProjectionEvidence AS e
            INNER JOIN dbo.OperationalMetricProjection AS p
                ON p.OperationalMetricProjectionRowId = e.OperationalMetricProjectionRowId
            WHERE p.OperationalMetricProjectionProcessorRowId = @ProcessorRowId
            ORDER BY e.OperationalMetricProjectionRowId, e.EvidenceKind, e.EvidenceOrdinal;
            """;
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = context.ProjectionProcessorRowId;

        var rows = new List<ObservedEvidenceRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new ObservedEvidenceRow(
                reader.GetInt64(0),
                reader.GetByte(1),
                reader.GetInt32(2),
                reader.GetString(3),
                (byte[])reader[4],
                GetNullableString(reader, 5),
                reader.IsDBNull(6) ? null : reader.GetByte(6),
                GetNullableString(reader, 7),
                GetNullableString(reader, 8),
                reader.IsDBNull(9) ? null : reader.GetDecimal(9),
                reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
                reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
                GetNullableString(reader, 12),
                GetNullableString(reader, 13),
                reader.IsDBNull(14) ? null : reader.GetInt16(14),
                reader.IsDBNull(15) ? null : (byte[])reader[15],
                reader.IsDBNull(16) ? null : (byte[])reader[16]));
        }
        return rows;
    }

    private static void AddProjectionParameters(
        SqlCommand command,
        long processorRowId,
        SqlServerOperationalMetricProjectionWriteModel model)
    {
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
        command.Parameters.Add("@EvaluationKeyCodecVersion", SqlDbType.SmallInt).Value = OperationalMetricEvaluationKeyV1Codec.CodecVersion;
        command.Parameters.Add("@EvaluationKeyHash", SqlDbType.Binary, 32).Value = model.EvaluationKeyHash;
        command.Parameters.Add("@EvaluationKeyBinary", SqlDbType.VarBinary, -1).Value = model.EvaluationKeyBinary;
        command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value = model.Projection.Key.MachineId.Value;
        command.Parameters.Add("@PeriodKind", SqlDbType.TinyInt).Value = model.PeriodKind;
        AddString(command, "@PeriodSiteId", 256, model.PeriodSiteId);
        command.Parameters.Add("@PeriodSiteOrderKey", SqlDbType.VarBinary, StringOrderKeyV2Codec.MaximumEncodedLength).Value = model.PeriodSiteOrderKey;
        AddNullableString(command, "@ShiftScheduleAssignmentId", 256, model.ShiftScheduleAssignmentId);
        AddNullableBytes(command, "@ShiftScheduleAssignmentOrderKey", StringOrderKeyV2Codec.MaximumEncodedLength, model.ShiftScheduleAssignmentOrderKey);
        AddNullableString(command, "@ShiftId", 256, model.ShiftId);
        AddNullableBytes(command, "@ShiftOrderKey", StringOrderKeyV2Codec.MaximumEncodedLength, model.ShiftOrderKey);
        AddNullable(command, "@ShiftStartsAtUtc", SqlDbType.DateTimeOffset, model.ShiftStartsAtUtc);
        AddNullable(command, "@ShiftEndsAtUtc", SqlDbType.DateTimeOffset, model.ShiftEndsAtUtc);
        AddNullable(command, "@ProductionBusinessDate", SqlDbType.Date, model.ProductionBusinessDate);
        AddOptionalIdentity(command, "ProductionOrder", model.ProductionOrderId, model.ProductionOrderOrderKey);
        AddOptionalIdentity(command, "Operation", model.OperationId, model.OperationOrderKey);
        AddOptionalIdentity(command, "Part", model.PartId, model.PartOrderKey);
        AddOptionalIdentity(command, "Operator", model.OperatorId, model.OperatorOrderKey);
        AddString(command, "@MetricKey", 256, model.MetricKey);
        command.Parameters.Add("@MetricKeyOrderKey", SqlDbType.VarBinary, StringOrderKeyV2Codec.MaximumEncodedLength).Value = model.MetricKeyOrderKey;
        AddString(command, "@DefinitionVersion", 256, model.DefinitionVersion);
        command.Parameters.Add("@DefinitionVersionOrderKey", SqlDbType.VarBinary, StringOrderKeyV2Codec.MaximumEncodedLength).Value = model.DefinitionVersionOrderKey;
        AddProjectionStateParameters(command, model);
    }

    private static void AddProjectionStateParameters(
        SqlCommand command,
        SqlServerOperationalMetricProjectionWriteModel model)
    {
        var projection = model.Projection;
        command.Parameters.Add("@Status", SqlDbType.TinyInt).Value = checked((byte)projection.Status);
        AddNullableString(command, "@MetricValue", 64, model.MetricValue);
        AddString(command, "@Unit", 128, projection.Unit);
        AddNullable(command, "@ReasonCode", SqlDbType.TinyInt,
            projection.ReasonCode is null ? null : checked((byte)projection.ReasonCode.Value));
        AddNullableString(command, "@ReasonOperandName", 256, projection.ReasonOperandName);
        command.Parameters.Add(SqlServerUInt64.CreateParameter(
            "@SourceRevisionPosition",
            projection.SourceRevision.Position.Value));
    }

    private static void AddOptionalIdentity(
        SqlCommand command,
        string prefix,
        string? value,
        byte[]? orderKey)
    {
        command.Parameters.Add($"@{prefix}Present", SqlDbType.Bit).Value = value is not null;
        AddNullableString(command, $"@{prefix}Id", 256, value);
        AddNullableBytes(command, $"@{prefix}OrderKey", StringOrderKeyV2Codec.MaximumEncodedLength, orderKey);
    }

    private static void AddString(SqlCommand command, string name, int size, string value) =>
        command.Parameters.Add(name, SqlDbType.NVarChar, size).Value = value;

    private static void AddNullableString(SqlCommand command, string name, int size, string? value) =>
        command.Parameters.Add(name, SqlDbType.NVarChar, size).Value = (object?)value ?? DBNull.Value;

    private static void AddNullableBytes(SqlCommand command, string name, int size, byte[]? value) =>
        command.Parameters.Add(name, SqlDbType.VarBinary, size).Value = (object?)value ?? DBNull.Value;

    private static void AddNullable(SqlCommand command, string name, SqlDbType type, object? value) =>
        command.Parameters.Add(name, type).Value = value ?? DBNull.Value;

    private static bool NullableStringEquals(SqlDataReader reader, int ordinal, string? expected) =>
        reader.IsDBNull(ordinal)
            ? expected is null
            : string.Equals(reader.GetString(ordinal), expected, StringComparison.Ordinal);

    private static bool NullableByteEquals(SqlDataReader reader, int ordinal, byte? expected) =>
        reader.IsDBNull(ordinal)
            ? expected is null
            : expected == reader.GetByte(ordinal);

    private static string? GetNullableString(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static async Task InjectAsync(
        Func<SqlServerOperationalMetricProjectionPublicationStage, CancellationToken, Task>? failureInjection,
        SqlServerOperationalMetricProjectionPublicationStage stage,
        CancellationToken cancellationToken)
    {
        if (failureInjection is not null)
        {
            await failureInjection(stage, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record ExpectedEvidenceRow(
        long ProjectionRowId,
        byte EvidenceKind,
        int EvidenceOrdinal,
        string OperandName,
        byte[] OperandNameOrderKey,
        string? ComponentKey,
        byte? MetricDimension,
        string? ComponentValue,
        string? ComponentUnit,
        decimal? InputCount,
        DateTimeOffset? FirstInputTimestamp,
        DateTimeOffset? LastInputTimestamp,
        string? DependencyMetricKey,
        string? DependencyDefinitionVersion,
        short? DependencySnapshotCodecVersion,
        byte[]? DependencySnapshotHash,
        byte[]? DependencySnapshotBinary)
    {
        public static ExpectedEvidenceRow ForComponent(
            long projectionRowId,
            int ordinal,
            OperationalMetricComponentProjectionEvidence evidence) =>
            new(
                projectionRowId,
                1,
                ordinal,
                evidence.OperandName,
                StringOrderKeyV2Codec.Encode(evidence.OperandName),
                evidence.SourceIdentity.ComponentKey,
                checked((byte)evidence.Dimension),
                CanonicalDecimalTextV1Codec.Serialize(evidence.Value),
                evidence.Unit,
                Convert.ToDecimal(evidence.InputCount, CultureInfo.InvariantCulture),
                evidence.FirstInputTimestamp,
                evidence.LastInputTimestamp,
                null,
                null,
                null,
                null,
                null);

        public static ExpectedEvidenceRow ForDependency(
            long projectionRowId,
            int ordinal,
            OperationalMetricDependencyProjectionEvidence evidence)
        {
            var snapshot = OperationalMetricDependencySnapshotV1Codec.Encode(evidence.Projection);
            return new ExpectedEvidenceRow(
                projectionRowId,
                2,
                ordinal,
                evidence.OperandName,
                StringOrderKeyV2Codec.Encode(evidence.OperandName),
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                evidence.DefinitionId.MetricKey,
                evidence.DefinitionId.Version,
                OperationalMetricDependencySnapshotV1Codec.CodecVersion,
                OperationalMetricDependencySnapshotV1Codec.ComputeHash(snapshot),
                snapshot);
        }

        public bool ExactEquals(ObservedEvidenceRow observed) =>
            ProjectionRowId == observed.ProjectionRowId &&
            EvidenceKind == observed.EvidenceKind &&
            EvidenceOrdinal == observed.EvidenceOrdinal &&
            string.Equals(OperandName, observed.OperandName, StringComparison.Ordinal) &&
            OperandNameOrderKey.AsSpan().SequenceEqual(observed.OperandNameOrderKey) &&
            string.Equals(ComponentKey, observed.ComponentKey, StringComparison.Ordinal) &&
            MetricDimension == observed.MetricDimension &&
            string.Equals(ComponentValue, observed.ComponentValue, StringComparison.Ordinal) &&
            string.Equals(ComponentUnit, observed.ComponentUnit, StringComparison.Ordinal) &&
            InputCount == observed.InputCount &&
            FirstInputTimestamp == observed.FirstInputTimestamp &&
            LastInputTimestamp == observed.LastInputTimestamp &&
            string.Equals(DependencyMetricKey, observed.DependencyMetricKey, StringComparison.Ordinal) &&
            string.Equals(DependencyDefinitionVersion, observed.DependencyDefinitionVersion, StringComparison.Ordinal) &&
            DependencySnapshotCodecVersion == observed.DependencySnapshotCodecVersion &&
            BytesEqual(DependencySnapshotHash, observed.DependencySnapshotHash) &&
            BytesEqual(DependencySnapshotBinary, observed.DependencySnapshotBinary);

        private static bool BytesEqual(byte[]? left, byte[]? right) =>
            left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);
    }

    private sealed record ObservedEvidenceRow(
        long ProjectionRowId,
        byte EvidenceKind,
        int EvidenceOrdinal,
        string OperandName,
        byte[] OperandNameOrderKey,
        string? ComponentKey,
        byte? MetricDimension,
        string? ComponentValue,
        string? ComponentUnit,
        decimal? InputCount,
        DateTimeOffset? FirstInputTimestamp,
        DateTimeOffset? LastInputTimestamp,
        string? DependencyMetricKey,
        string? DependencyDefinitionVersion,
        short? DependencySnapshotCodecVersion,
        byte[]? DependencySnapshotHash,
        byte[]? DependencySnapshotBinary);
}
