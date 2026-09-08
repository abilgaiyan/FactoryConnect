using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed class SqlServerOperationalMetricProjectionQueryReader : IOperationalMetricProjectionQueryReader
{
    private readonly string _connectionString;
    private readonly SqlServerOperationalMetricProjectionSummaryReader _summaryReader;

    public SqlServerOperationalMetricProjectionQueryReader(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _summaryReader = new SqlServerOperationalMetricProjectionSummaryReader(connectionString);
    }

    public ValueTask<IReadOnlyList<OperationalMetricProjectionSummary>> ReadPeriodSummariesAsync(
        OperationalMetricProjectionProcessorId processorId,
        MachineId machineId,
        OperationalMetricPeriodId periodId,
        OperationalMetricEvaluationContextKey contextKey,
        CancellationToken cancellationToken) =>
        _summaryReader.ReadPeriodSummariesAsync(
            processorId,
            machineId,
            periodId,
            contextKey,
            cancellationToken);

    public async ValueTask<OperationalMetricProjection?> ReadDetailAsync(
        OperationalMetricProjectionProcessorId processorId,
        OperationalMetricEvaluationKey key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var source = await ResolveProcessorSourceAsync(connection, processorId, cancellationToken)
            .ConfigureAwait(false);
        if (source is null)
        {
            return null;
        }

        return await SqlServerOperationalMetricProjectionStableRead.ExecuteAsync(
            connection,
            source.ProjectionProcessorRowId,
            async (stableConnection, token) =>
            {
                var manifestRowIds = await ReadManifestRowIdsAsync(
                    stableConnection,
                    source.ProjectionProcessorRowId,
                    token).ConfigureAwait(false);

                if (manifestRowIds.Count == 0)
                {
                    return null;
                }

                var rows = await ReadManifestProjectionRowsAsync(
                    stableConnection,
                    source.ProjectionProcessorRowId,
                    token).ConfigureAwait(false);

                ValidateManifestCoverage(manifestRowIds, rows);

                ProjectionRow? selectedRow = null;
                OperationalMetricProjectionSummary? selectedSummary = null;
                foreach (var row in rows)
                {
                    var summary = MaterializeSummary(processorId, source, row);
                    if (summary.Key != key)
                    {
                        continue;
                    }

                    if (selectedRow is not null)
                    {
                        throw Corrupt("duplicate current-manifest evaluation key");
                    }

                    selectedRow = row;
                    selectedSummary = summary;
                }

                if (selectedRow is null || selectedSummary is null)
                {
                    return null;
                }

                var evidenceRows = await ReadEvidenceAsync(
                    stableConnection,
                    selectedRow.RowId,
                    token).ConfigureAwait(false);

                return MaterializeProjection(selectedSummary, evidenceRows);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ProcessorSource?> ResolveProcessorSourceAsync(
        SqlConnection connection,
        OperationalMetricProjectionProcessorId processorId,
        CancellationToken cancellationToken)
    {
        var projectionProcessorKeyBinary = StringOrderKeyV2Codec.Encode(processorId.Value);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT pp.OperationalMetricProjectionProcessorRowId, pp.ProcessorKey, pp.ProcessorKeyBinary, " +
            "pp.MetricAggregationProcessorRowId, pp.MetricInputStreamRowId, " +
            "ap.ProcessorKey, ap.ProcessorKeyBinary, ap.MetricInputStreamRowId, " +
            "s.MachineId, s.StreamKey, s.StreamKeyBinary " +
            "FROM dbo.OperationalMetricProjectionProcessor AS pp " +
            "INNER JOIN dbo.MetricAggregationProcessor AS ap " +
            "ON ap.MetricAggregationProcessorRowId = pp.MetricAggregationProcessorRowId " +
            "INNER JOIN dbo.MetricInputStream AS s " +
            "ON s.MetricInputStreamRowId = pp.MetricInputStreamRowId " +
            "WHERE pp.ProcessorKeyBinary = @ProcessorKeyBinary;";
        command.Parameters.Add(
            "@ProcessorKeyBinary",
            SqlDbType.VarBinary,
            StringOrderKeyV2Codec.MaximumEncodedLength).Value = projectionProcessorKeyBinary;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var persistedProjectionProcessorKey = reader.GetString(1);
        var persistedProjectionProcessorKeyBinary = (byte[])reader[2];
        if (!string.Equals(persistedProjectionProcessorKey, processorId.Value, StringComparison.Ordinal) ||
            !persistedProjectionProcessorKeyBinary.AsSpan().SequenceEqual(projectionProcessorKeyBinary))
        {
            throw Corrupt("projection processor identity");
        }

        var aggregationProcessorRowId = reader.GetInt64(3);
        var streamRowId = reader.GetInt64(4);
        var aggregationProcessorKey = reader.GetString(5);
        var aggregationProcessorKeyBinary = (byte[])reader[6];
        var aggregationStreamRowId = reader.GetInt64(7);
        var machineId = new MachineId(reader.GetGuid(8));
        var streamKey = reader.GetString(9);
        var streamKeyBinary = (byte[])reader[10];

        if (aggregationStreamRowId != streamRowId ||
            !aggregationProcessorKeyBinary.AsSpan().SequenceEqual(OrdinalStringKeyCodec.Encode(aggregationProcessorKey)) ||
            !streamKeyBinary.AsSpan().SequenceEqual(OrdinalStringKeyCodec.Encode(streamKey)))
        {
            throw Corrupt("projection processor source binding");
        }

        return new ProcessorSource(
            reader.GetInt64(0),
            aggregationProcessorRowId,
            streamRowId,
            new MetricAggregationProcessorId(aggregationProcessorKey),
            new MetricInputStreamId(machineId, streamKey));
    }

    private static async Task<IReadOnlyList<long>> ReadManifestRowIdsAsync(
        SqlConnection connection,
        long projectionProcessorRowId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT OperationalMetricProjectionRowId " +
            "FROM dbo.OperationalMetricProjectionManifest " +
            "WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId " +
            "ORDER BY OperationalMetricProjectionRowId;";
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = projectionProcessorRowId;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rowIds = new List<long>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rowIds.Add(reader.GetInt64(0));
        }

        return rowIds;
    }

    private static async Task<IReadOnlyList<ProjectionRow>> ReadManifestProjectionRowsAsync(
        SqlConnection connection,
        long projectionProcessorRowId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT p.OperationalMetricProjectionRowId, p.EvaluationKeyCodecVersion, p.EvaluationKeyHash, p.EvaluationKeyBinary, " +
            "p.MachineId, p.PeriodKind, p.PeriodSiteId, p.PeriodSiteOrderKey, " +
            "p.ShiftScheduleAssignmentId, p.ShiftScheduleAssignmentOrderKey, p.ShiftId, p.ShiftOrderKey, " +
            "p.ShiftStartsAtUtc, p.ShiftEndsAtUtc, p.ProductionBusinessDate, " +
            "p.ProductionOrderPresent, p.ProductionOrderId, p.ProductionOrderOrderKey, " +
            "p.OperationPresent, p.OperationId, p.OperationOrderKey, " +
            "p.PartPresent, p.PartId, p.PartOrderKey, " +
            "p.OperatorPresent, p.OperatorId, p.OperatorOrderKey, " +
            "p.MetricKey, p.MetricKeyOrderKey, p.DefinitionVersion, p.DefinitionVersionOrderKey, " +
            "p.Status, p.MetricValue, p.Unit, p.ReasonCode, p.ReasonOperandName, p.SourceRevisionPosition " +
            "FROM dbo.OperationalMetricProjectionManifest AS m " +
            "INNER JOIN dbo.OperationalMetricProjection AS p " +
            "ON p.OperationalMetricProjectionProcessorRowId = m.OperationalMetricProjectionProcessorRowId " +
            "AND p.OperationalMetricProjectionRowId = m.OperationalMetricProjectionRowId " +
            "WHERE m.OperationalMetricProjectionProcessorRowId = @ProcessorRowId " +
            "ORDER BY m.OperationalMetricProjectionRowId;";
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = projectionProcessorRowId;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<ProjectionRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(MaterializeRow(reader));
        }

        return rows;
    }

    private static void ValidateManifestCoverage(
        IReadOnlyList<long> manifestRowIds,
        IReadOnlyList<ProjectionRow> rows)
    {
        if (manifestRowIds.Count != rows.Count)
        {
            throw Corrupt("manifest projection coverage");
        }

        for (var index = 0; index < manifestRowIds.Count; index++)
        {
            if (manifestRowIds[index] != rows[index].RowId)
            {
                throw Corrupt("manifest projection identity");
            }
        }
    }

    private static OperationalMetricProjectionSummary MaterializeSummary(
        OperationalMetricProjectionProcessorId processorId,
        ProcessorSource source,
        ProjectionRow row)
    {
        try
        {
            var key = MaterializeKey(row);
            ValidateCanonicalIdentity(row, key);

            if (!Enum.IsDefined(typeof(OperationalMetricEvaluationStatus), (int)row.Status))
            {
                throw Corrupt("projection status");
            }

            var status = (OperationalMetricEvaluationStatus)row.Status;
            OperationalMetricEvaluationReasonCode? reasonCode = null;
            if (row.ReasonCode is not null)
            {
                if (!Enum.IsDefined(typeof(OperationalMetricEvaluationReasonCode), (int)row.ReasonCode.Value))
                {
                    throw Corrupt("projection reason code");
                }

                reasonCode = (OperationalMetricEvaluationReasonCode)row.ReasonCode.Value;
            }

            decimal? value = null;
            if (row.MetricValue is not null)
            {
                if (!decimal.TryParse(
                        row.MetricValue,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var parsed) ||
                    !string.Equals(
                        CanonicalDecimalTextV1Codec.Serialize(parsed),
                        row.MetricValue,
                        StringComparison.Ordinal))
                {
                    throw Corrupt("projection metric value");
                }

                value = parsed;
            }

            var sourceRevision = new MetricAggregationCheckpoint(
                source.AggregationProcessorId,
                source.StreamId,
                row.SourceRevisionPosition);

            return new OperationalMetricProjectionSummary(
                processorId,
                key,
                status,
                value,
                row.Unit,
                reasonCode,
                row.ReasonOperandName,
                sourceRevision);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or OverflowException or FormatException)
        {
            throw new InvalidOperationException(
                "Persisted operational metric projection summary is corrupt.",
                exception);
        }
    }

    private static OperationalMetricProjection MaterializeProjection(
        OperationalMetricProjectionSummary summary,
        IReadOnlyList<EvidenceRow> evidenceRows)
    {
        try
        {
            var componentRows = evidenceRows.Where(static row => row.EvidenceKind == 1).ToArray();
            var dependencyRows = evidenceRows.Where(static row => row.EvidenceKind == 2).ToArray();

            if (componentRows.Length + dependencyRows.Length != evidenceRows.Count)
            {
                throw Corrupt("evidence kind");
            }

            ValidateEvidenceOrdinals(componentRows);
            ValidateEvidenceOrdinals(dependencyRows);

            var components = componentRows
                .Select(row => MaterializeComponentEvidence(summary, row))
                .ToArray();
            var dependencies = dependencyRows
                .Select(MaterializeDependencyEvidence)
                .ToArray();

            return new OperationalMetricProjection(
                summary.ProcessorId,
                summary.Key,
                summary.Status,
                summary.Value,
                summary.Unit,
                summary.ReasonCode,
                summary.ReasonOperandName,
                summary.SourceRevision,
                new ReadOnlyCollection<OperationalMetricComponentProjectionEvidence>(components),
                new ReadOnlyCollection<OperationalMetricDependencyProjectionEvidence>(dependencies));
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or OverflowException or FormatException)
        {
            throw new InvalidOperationException(
                "Persisted operational metric projection detail is corrupt.",
                exception);
        }
    }

    private static OperationalMetricComponentProjectionEvidence MaterializeComponentEvidence(
        OperationalMetricProjectionSummary summary,
        EvidenceRow row)
    {
        ValidateCommonEvidence(row);
        if (row.ComponentKey is null ||
            row.MetricDimension is null ||
            row.ComponentValue is null ||
            row.ComponentUnit is null ||
            row.InputCount is null ||
            row.FirstInputTimestamp is null ||
            row.LastInputTimestamp is null ||
            row.DependencyMetricKey is not null ||
            row.DependencyDefinitionVersion is not null ||
            row.DependencySnapshotCodecVersion is not null ||
            row.DependencySnapshotHash is not null ||
            row.DependencySnapshotBinary is not null)
        {
            throw Corrupt("component evidence subtype shape");
        }

        if (!Enum.IsDefined(typeof(MetricDimension), (int)row.MetricDimension.Value))
        {
            throw Corrupt("component evidence dimension");
        }

        if (!decimal.TryParse(
                row.ComponentValue,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value) ||
            !string.Equals(
                CanonicalDecimalTextV1Codec.Serialize(value),
                row.ComponentValue,
                StringComparison.Ordinal))
        {
            throw Corrupt("component evidence value");
        }

        if (row.InputCount.Value <= 0 ||
            row.InputCount.Value > long.MaxValue ||
            decimal.Truncate(row.InputCount.Value) != row.InputCount.Value)
        {
            throw Corrupt("component evidence input count");
        }

        if (row.FirstInputTimestamp.Value.Offset != TimeSpan.Zero ||
            row.LastInputTimestamp.Value.Offset != TimeSpan.Zero)
        {
            throw Corrupt("component evidence UTC timestamp");
        }

        return new OperationalMetricComponentProjectionEvidence(
            row.OperandName,
            new OperationalMetricAggregateSourceIdentity(
                summary.SourceRevision.ProcessorId,
                summary.Key.MachineId,
                summary.Key.PeriodId,
                row.ComponentKey),
            summary.SourceRevision,
            (MetricDimension)row.MetricDimension.Value,
            value,
            row.ComponentUnit,
            decimal.ToInt64(row.InputCount.Value),
            row.FirstInputTimestamp.Value,
            row.LastInputTimestamp.Value);
    }

    private static OperationalMetricDependencyProjectionEvidence MaterializeDependencyEvidence(EvidenceRow row)
    {
        ValidateCommonEvidence(row);
        if (row.ComponentKey is not null ||
            row.MetricDimension is not null ||
            row.ComponentValue is not null ||
            row.ComponentUnit is not null ||
            row.InputCount is not null ||
            row.FirstInputTimestamp is not null ||
            row.LastInputTimestamp is not null ||
            row.DependencyMetricKey is null ||
            row.DependencyDefinitionVersion is null ||
            row.DependencySnapshotCodecVersion is null ||
            row.DependencySnapshotHash is null ||
            row.DependencySnapshotBinary is null)
        {
            throw Corrupt("dependency evidence subtype shape");
        }

        if (row.DependencySnapshotCodecVersion != OperationalMetricDependencySnapshotV1Codec.CodecVersion ||
            !OperationalMetricDependencySnapshotV1Codec.ComputeHash(row.DependencySnapshotBinary)
                .AsSpan().SequenceEqual(row.DependencySnapshotHash))
        {
            throw Corrupt("dependency evidence snapshot identity");
        }

        var projection = OperationalMetricDependencySnapshotV1Decoder.Decode(row.DependencySnapshotBinary);
        var definitionId = new OperationalMetricDefinitionId(
            row.DependencyMetricKey,
            row.DependencyDefinitionVersion);

        if (projection.Key.DefinitionId != definitionId)
        {
            throw Corrupt("dependency evidence definition identity");
        }

        return new OperationalMetricDependencyProjectionEvidence(
            row.OperandName,
            definitionId,
            projection);
    }

    private static void ValidateCommonEvidence(EvidenceRow row)
    {
        if (row.EvidenceOrdinal < 0 ||
            !row.OperandNameOrderKey.AsSpan().SequenceEqual(StringOrderKeyV2Codec.Encode(row.OperandName)))
        {
            throw Corrupt("evidence ordinal/name identity");
        }
    }

    private static void ValidateEvidenceOrdinals(IReadOnlyList<EvidenceRow> rows)
    {
        for (var index = 0; index < rows.Count; index++)
        {
            if (rows[index].EvidenceOrdinal != index)
            {
                throw Corrupt("evidence ordinal sequence");
            }
        }
    }

    private static async Task<IReadOnlyList<EvidenceRow>> ReadEvidenceAsync(
        SqlConnection connection,
        long projectionRowId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT EvidenceKind, EvidenceOrdinal, OperandName, OperandNameOrderKey, " +
            "ComponentKey, MetricDimension, ComponentValue, ComponentUnit, InputCount, " +
            "FirstInputTimestamp, LastInputTimestamp, DependencyMetricKey, DependencyDefinitionVersion, " +
            "DependencySnapshotCodecVersion, DependencySnapshotHash, DependencySnapshotBinary " +
            "FROM dbo.OperationalMetricProjectionEvidence " +
            "WHERE OperationalMetricProjectionRowId = @ProjectionRowId " +
            "ORDER BY EvidenceKind, EvidenceOrdinal;";
        command.Parameters.Add("@ProjectionRowId", SqlDbType.BigInt).Value = projectionRowId;

        var rows = new List<EvidenceRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new EvidenceRow(
                reader.GetByte(0),
                reader.GetInt32(1),
                reader.GetString(2),
                (byte[])reader[3],
                GetNullableString(reader, 4),
                reader.IsDBNull(5) ? null : reader.GetByte(5),
                GetNullableString(reader, 6),
                GetNullableString(reader, 7),
                reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                GetNullableDateTimeOffset(reader, 9),
                GetNullableDateTimeOffset(reader, 10),
                GetNullableString(reader, 11),
                GetNullableString(reader, 12),
                reader.IsDBNull(13) ? null : reader.GetInt16(13),
                GetNullableBytes(reader, 14),
                GetNullableBytes(reader, 15)));
        }

        return rows;
    }

    private static OperationalMetricEvaluationKey MaterializeKey(ProjectionRow row)
    {
        var machineId = new MachineId(row.MachineId);
        OperationalMetricPeriodId periodId = row.PeriodKind switch
        {
            1 when row.ShiftScheduleAssignmentId is not null &&
                   row.ShiftId is not null &&
                   row.ShiftStartsAtUtc is not null &&
                   row.ShiftEndsAtUtc is not null &&
                   row.ProductionBusinessDate is null =>
                new OperationalMetricPeriodId.Shift(
                    new ShiftOccurrenceId(
                        new SiteId(row.PeriodSiteId),
                        new ShiftScheduleAssignmentId(row.ShiftScheduleAssignmentId),
                        new ShiftId(row.ShiftId),
                        row.ShiftStartsAtUtc.Value,
                        row.ShiftEndsAtUtc.Value)),
            2 when row.ShiftScheduleAssignmentId is null &&
                   row.ShiftId is null &&
                   row.ShiftStartsAtUtc is null &&
                   row.ShiftEndsAtUtc is null &&
                   row.ProductionBusinessDate is not null =>
                new OperationalMetricPeriodId.ProductionDay(
                    new ProductionDayId(
                        new SiteId(row.PeriodSiteId),
                        row.ProductionBusinessDate.Value)),
            _ => throw Corrupt("projection period shape"),
        };

        var context = new OperationalMetricEvaluationContextKey
        {
            ProductionOrderId = MaterializeOptional(
                row.ProductionOrderPresent,
                row.ProductionOrderId,
                static value => new ProductionOrderId(value)),
            OperationId = MaterializeOptional(
                row.OperationPresent,
                row.OperationId,
                static value => new OperationId(value)),
            PartId = MaterializeOptional(
                row.PartPresent,
                row.PartId,
                static value => new PartId(value)),
            OperatorId = MaterializeOptional(
                row.OperatorPresent,
                row.OperatorId,
                static value => new OperatorId(value)),
        };

        return new OperationalMetricEvaluationKey(
            machineId,
            periodId,
            new OperationalMetricDefinitionId(row.MetricKey, row.DefinitionVersion),
            context);
    }

    private static T? MaterializeOptional<T>(
        bool present,
        string? value,
        Func<string, T> factory)
        where T : struct
    {
        if (present != (value is not null))
        {
            throw Corrupt("projection optional identity presence");
        }

        return value is null ? null : factory(value);
    }

    private static void ValidateCanonicalIdentity(
        ProjectionRow row,
        OperationalMetricEvaluationKey key)
    {
        if (row.CodecVersion != OperationalMetricEvaluationKeyV1Codec.CodecVersion)
        {
            throw Corrupt("evaluation-key codec version");
        }

        var canonicalBinary = OperationalMetricEvaluationKeyV1Codec.Encode(key);
        if (!canonicalBinary.AsSpan().SequenceEqual(row.EvaluationKeyBinary) ||
            !OperationalMetricEvaluationKeyV1Codec.ComputeHash(row.EvaluationKeyBinary)
                .AsSpan().SequenceEqual(row.EvaluationKeyHash))
        {
            throw Corrupt("evaluation-key binary/hash identity");
        }

        ValidateOrderKey(row.PeriodSiteId, row.PeriodSiteOrderKey);
        ValidateOptionalOrderKey(
            row.ProductionOrderPresent,
            row.ProductionOrderId,
            row.ProductionOrderOrderKey);
        ValidateOptionalOrderKey(row.OperationPresent, row.OperationId, row.OperationOrderKey);
        ValidateOptionalOrderKey(row.PartPresent, row.PartId, row.PartOrderKey);
        ValidateOptionalOrderKey(row.OperatorPresent, row.OperatorId, row.OperatorOrderKey);
        ValidateOrderKey(row.MetricKey, row.MetricKeyOrderKey);
        ValidateOrderKey(row.DefinitionVersion, row.DefinitionVersionOrderKey);

        if (row.PeriodKind == 1)
        {
            ValidateOptionalOrderKey(true, row.ShiftScheduleAssignmentId, row.ShiftScheduleAssignmentOrderKey);
            ValidateOptionalOrderKey(true, row.ShiftId, row.ShiftOrderKey);
        }
        else
        {
            ValidateOptionalOrderKey(false, row.ShiftScheduleAssignmentId, row.ShiftScheduleAssignmentOrderKey);
            ValidateOptionalOrderKey(false, row.ShiftId, row.ShiftOrderKey);
        }
    }

    private static void ValidateOrderKey(string value, byte[] orderKey)
    {
        if (!orderKey.AsSpan().SequenceEqual(StringOrderKeyV2Codec.Encode(value)))
        {
            throw Corrupt("projection text/order-key identity");
        }
    }

    private static void ValidateOptionalOrderKey(bool present, string? value, byte[]? orderKey)
    {
        if (present != (value is not null) || present != (orderKey is not null))
        {
            throw Corrupt("projection optional text/order-key shape");
        }

        if (value is not null &&
            !orderKey!.AsSpan().SequenceEqual(StringOrderKeyV2Codec.Encode(value)))
        {
            throw Corrupt("projection optional text/order-key identity");
        }
    }

    private static ProjectionRow MaterializeRow(SqlDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetInt16(1),
            (byte[])reader[2],
            (byte[])reader[3],
            reader.GetGuid(4),
            reader.GetByte(5),
            reader.GetString(6),
            (byte[])reader[7],
            GetNullableString(reader, 8),
            GetNullableBytes(reader, 9),
            GetNullableString(reader, 10),
            GetNullableBytes(reader, 11),
            GetNullableDateTimeOffset(reader, 12),
            GetNullableDateTimeOffset(reader, 13),
            GetNullableDateOnly(reader, 14),
            reader.GetBoolean(15),
            GetNullableString(reader, 16),
            GetNullableBytes(reader, 17),
            reader.GetBoolean(18),
            GetNullableString(reader, 19),
            GetNullableBytes(reader, 20),
            reader.GetBoolean(21),
            GetNullableString(reader, 22),
            GetNullableBytes(reader, 23),
            reader.GetBoolean(24),
            GetNullableString(reader, 25),
            GetNullableBytes(reader, 26),
            reader.GetString(27),
            (byte[])reader[28],
            reader.GetString(29),
            (byte[])reader[30],
            reader.GetByte(31),
            GetNullableString(reader, 32),
            reader.GetString(33),
            reader.IsDBNull(34) ? null : reader.GetByte(34),
            GetNullableString(reader, 35),
            new MetricInputPosition(SqlServerUInt64.Materialize(reader.GetDecimal(36))));

    private static string? GetNullableString(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static byte[]? GetNullableBytes(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : (byte[])reader[ordinal];

    private static DateTimeOffset? GetNullableDateTimeOffset(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDateTimeOffset(ordinal);

    private static DateOnly? GetNullableDateOnly(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : DateOnly.FromDateTime(reader.GetDateTime(ordinal));

    private static InvalidOperationException Corrupt(string field) =>
        new($"Persisted operational metric projection {field} is corrupt or unsupported.");

    private sealed record ProcessorSource(
        long ProjectionProcessorRowId,
        long AggregationProcessorRowId,
        long StreamRowId,
        MetricAggregationProcessorId AggregationProcessorId,
        MetricInputStreamId StreamId);

    private sealed record ProjectionRow(
        long RowId,
        short CodecVersion,
        byte[] EvaluationKeyHash,
        byte[] EvaluationKeyBinary,
        Guid MachineId,
        byte PeriodKind,
        string PeriodSiteId,
        byte[] PeriodSiteOrderKey,
        string? ShiftScheduleAssignmentId,
        byte[]? ShiftScheduleAssignmentOrderKey,
        string? ShiftId,
        byte[]? ShiftOrderKey,
        DateTimeOffset? ShiftStartsAtUtc,
        DateTimeOffset? ShiftEndsAtUtc,
        DateOnly? ProductionBusinessDate,
        bool ProductionOrderPresent,
        string? ProductionOrderId,
        byte[]? ProductionOrderOrderKey,
        bool OperationPresent,
        string? OperationId,
        byte[]? OperationOrderKey,
        bool PartPresent,
        string? PartId,
        byte[]? PartOrderOrderKey,
        bool OperatorPresent,
        string? OperatorId,
        byte[]? OperatorOrderKey,
        string MetricKey,
        byte[] MetricKeyOrderKey,
        string DefinitionVersion,
        byte[] DefinitionVersionOrderKey,
        byte Status,
        string? MetricValue,
        string Unit,
        byte? ReasonCode,
        string? ReasonOperandName,
        MetricInputPosition SourceRevisionPosition);

    private sealed record EvidenceRow(
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
