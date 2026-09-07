using System.Data;
using System.Globalization;
using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed record SqlServerOperationalMetricProjectionResolvedRow(
    long ProjectionRowId,
    OperationalMetricProjection Projection,
    byte[] EvaluationKeyHash,
    byte[] EvaluationKeyBinary);

internal sealed record SqlServerOperationalMetricProjectionRowResolution(
    IReadOnlyList<SqlServerOperationalMetricProjectionResolvedRow> PublishedRows,
    IReadOnlyList<long> ObsoleteProjectionRowIds,
    IReadOnlyList<byte[]> LockedHashes);

internal static class SqlServerOperationalMetricProjectionRows
{
    public static async Task<SqlServerOperationalMetricProjectionRowResolution> ResolveAndPersistAsync(
        SqlServerOperationalMetricProjectionCommitContext context,
        OperationalMetricProjectionCommit commit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(commit);

        var incoming = commit.Projections.Select(Compile).ToArray();
        var incomingByHash = new Dictionary<string, ProjectionWriteModel>(StringComparer.Ordinal);
        foreach (var model in incoming)
        {
            var hashKey = Convert.ToHexString(model.Hash);
            if (incomingByHash.TryGetValue(hashKey, out var prior))
            {
                if (!prior.Binary.AsSpan().SequenceEqual(model.Binary))
                {
                    throw new InvalidOperationException("Operational metric evaluation-key SHA-256 collision detected within the proposed projection set.");
                }

                throw new InvalidOperationException("Duplicate operational metric evaluation-key identity detected within the proposed projection set.");
            }

            incomingByHash.Add(hashKey, model);
        }

        var currentHashes = await ReadCurrentHashesAsync(context, cancellationToken);
        var affectedByHex = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var hash in currentHashes)
        {
            affectedByHex[Convert.ToHexString(hash)] = hash;
        }
        foreach (var model in incoming)
        {
            affectedByHex[Convert.ToHexString(model.Hash)] = model.Hash;
        }

        var affected = affectedByHex.Values.ToList();
        affected.Sort(ByteArrayComparer.Instance);

        var existingByHash = new Dictionary<string, LockedProjectionRow>(StringComparer.Ordinal);
        foreach (var hash in affected)
        {
            var locked = await LockProjectionSlotAsync(context, hash, cancellationToken);
            if (locked is null)
            {
                continue;
            }

            ValidatePersistedHash(locked, hash);
            existingByHash.Add(Convert.ToHexString(hash), locked);
        }

        if (context.Mode == SqlServerOperationalMetricProjectionCommitMode.ReconcileProposed &&
            existingByHash.Count != incoming.Length)
        {
            throw new InvalidOperationException("Operational metric projection replay does not match the durable projection-row set.");
        }

        var published = new List<SqlServerOperationalMetricProjectionResolvedRow>(incoming.Length);
        foreach (var model in incoming)
        {
            var hashKey = Convert.ToHexString(model.Hash);
            if (existingByHash.TryGetValue(hashKey, out var existing))
            {
                ValidateExactIdentity(existing, model);
                ValidateStructuredIdentity(existing, model);

                if (context.Mode == SqlServerOperationalMetricProjectionCommitMode.ReconcileProposed)
                {
                    ValidateReplayState(existing, model);
                }
                else
                {
                    await UpdateProjectionStateAsync(context, existing.RowId, model, cancellationToken);
                }

                published.Add(new SqlServerOperationalMetricProjectionResolvedRow(
                    existing.RowId,
                    model.Projection,
                    model.Hash,
                    model.Binary));
            }
            else
            {
                if (context.Mode == SqlServerOperationalMetricProjectionCommitMode.ReconcileProposed)
                {
                    throw new InvalidOperationException("Operational metric projection replay is missing a durable projection row.");
                }

                var rowId = await InsertProjectionAsync(context, model, cancellationToken);
                published.Add(new SqlServerOperationalMetricProjectionResolvedRow(
                    rowId,
                    model.Projection,
                    model.Hash,
                    model.Binary));
            }
        }

        var incomingHashes = incomingByHash.Keys.ToHashSet(StringComparer.Ordinal);
        var obsolete = existingByHash
            .Where(pair => !incomingHashes.Contains(pair.Key))
            .Select(pair => pair.Value.RowId)
            .OrderBy(rowId => rowId)
            .ToArray();

        if (context.Mode == SqlServerOperationalMetricProjectionCommitMode.ReconcileProposed && obsolete.Length != 0)
        {
            throw new InvalidOperationException("Operational metric projection replay contains obsolete durable projection rows.");
        }

        return new SqlServerOperationalMetricProjectionRowResolution(
            published.OrderBy(row => row.ProjectionRowId).ToArray(),
            obsolete,
            affected.Select(hash => hash.ToArray()).ToArray());
    }

    public static async Task DeleteObsoleteProjectionRowsAsync(
        SqlServerOperationalMetricProjectionCommitContext context,
        IReadOnlyList<long> obsoleteProjectionRowIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(obsoleteProjectionRowIds);

        foreach (var rowId in obsoleteProjectionRowIds.OrderBy(value => value))
        {
            await using var command = context.Connection.CreateCommand();
            command.Transaction = context.Transaction;
            command.CommandText =
                "DELETE FROM dbo.OperationalMetricProjection " +
                "WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId " +
                "AND OperationalMetricProjectionRowId = @ProjectionRowId;";
            command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = context.ProjectionProcessorRowId;
            command.Parameters.Add("@ProjectionRowId", SqlDbType.BigInt).Value = rowId;
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("Operational metric projection row disappeared before dependency-safe deletion.");
            }
        }
    }

    private static ProjectionWriteModel Compile(OperationalMetricProjection projection)
    {
        var binary = OperationalMetricEvaluationKeyV1Codec.Encode(projection.Key);
        var hash = OperationalMetricEvaluationKeyV1Codec.ComputeHash(binary);
        var key = projection.Key;

        byte periodKind;
        string periodSiteId;
        string? shiftScheduleAssignmentId = null;
        string? shiftId = null;
        DateTimeOffset? shiftStartsAtUtc = null;
        DateTimeOffset? shiftEndsAtUtc = null;
        DateOnly? productionBusinessDate = null;

        switch (key.PeriodId)
        {
            case OperationalMetricPeriodId.Shift shift:
                periodKind = 1;
                periodSiteId = shift.ShiftOccurrenceId.SiteId.Value;
                shiftScheduleAssignmentId = shift.ShiftOccurrenceId.ShiftScheduleAssignmentId.Value;
                shiftId = shift.ShiftOccurrenceId.ShiftId.Value;
                shiftStartsAtUtc = shift.ShiftOccurrenceId.StartsAtUtc;
                shiftEndsAtUtc = shift.ShiftOccurrenceId.EndsAtUtc;
                break;
            case OperationalMetricPeriodId.ProductionDay productionDay:
                periodKind = 2;
                periodSiteId = productionDay.ProductionDayId.SiteId.Value;
                productionBusinessDate = productionDay.ProductionDayId.BusinessDate;
                break;
            default:
                throw new InvalidOperationException("Unsupported operational metric period type.");
        }

        var metricValue = projection.Value is null
            ? null
            : CanonicalDecimalTextV1Codec.Serialize(projection.Value.Value);

        return new ProjectionWriteModel(
            projection,
            binary,
            hash,
            periodKind,
            periodSiteId,
            StringOrderKeyV2Codec.Encode(periodSiteId),
            shiftScheduleAssignmentId,
            EncodeNullableOrderKey(shiftScheduleAssignmentId),
            shiftId,
            EncodeNullableOrderKey(shiftId),
            shiftStartsAtUtc,
            shiftEndsAtUtc,
            productionBusinessDate,
            key.ContextKey.ProductionOrderId?.Value,
            EncodeNullableOrderKey(key.ContextKey.ProductionOrderId?.Value),
            key.ContextKey.OperationId?.Value,
            EncodeNullableOrderKey(key.ContextKey.OperationId?.Value),
            key.ContextKey.PartId?.Value,
            EncodeNullableOrderKey(key.ContextKey.PartId?.Value),
            key.ContextKey.OperatorId?.Value,
            EncodeNullableOrderKey(key.ContextKey.OperatorId?.Value),
            key.DefinitionId.MetricKey,
            StringOrderKeyV2Codec.Encode(key.DefinitionId.MetricKey),
            key.DefinitionId.Version,
            StringOrderKeyV2Codec.Encode(key.DefinitionId.Version),
            metricValue);
    }

    private static byte[]? EncodeNullableOrderKey(string? value) =>
        value is null ? null : StringOrderKeyV2Codec.Encode(value);

    private static async Task<IReadOnlyList<byte[]>> ReadCurrentHashesAsync(
        SqlServerOperationalMetricProjectionCommitContext context,
        CancellationToken cancellationToken)
    {
        await using var command = context.Connection.CreateCommand();
        command.Transaction = context.Transaction;
        command.CommandText =
            "SELECT EvaluationKeyHash FROM dbo.OperationalMetricProjection " +
            "WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId;";
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = context.ProjectionProcessorRowId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var hashes = new List<byte[]>();
        while (await reader.ReadAsync(cancellationToken))
        {
            hashes.Add((byte[])reader[0]);
        }
        return hashes;
    }

    private static async Task<LockedProjectionRow?> LockProjectionSlotAsync(
        SqlServerOperationalMetricProjectionCommitContext context,
        byte[] hash,
        CancellationToken cancellationToken)
    {
        await using var command = context.Connection.CreateCommand();
        command.Transaction = context.Transaction;
        command.CommandText =
            "SELECT OperationalMetricProjectionRowId, EvaluationKeyCodecVersion, EvaluationKeyHash, EvaluationKeyBinary, " +
            "MachineId, PeriodKind, PeriodSiteId, PeriodSiteOrderKey, ShiftScheduleAssignmentId, ShiftScheduleAssignmentOrderKey, " +
            "ShiftId, ShiftOrderKey, ShiftStartsAtUtc, ShiftEndsAtUtc, ProductionBusinessDate, " +
            "ProductionOrderPresent, ProductionOrderId, ProductionOrderOrderKey, OperationPresent, OperationId, OperationOrderKey, " +
            "PartPresent, PartId, PartOrderKey, OperatorPresent, OperatorId, OperatorOrderKey, " +
            "MetricKey, MetricKeyOrderKey, DefinitionVersion, DefinitionVersionOrderKey, Status, MetricValue, Unit, ReasonCode, ReasonOperandName, SourceRevisionPosition " +
            "FROM dbo.OperationalMetricProjection WITH (UPDLOCK, HOLDLOCK) " +
            "WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId AND EvaluationKeyHash = @Hash;";
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = context.ProjectionProcessorRowId;
        command.Parameters.Add("@Hash", SqlDbType.Binary, 32).Value = hash;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new LockedProjectionRow(
            reader.GetInt64(0), reader.GetInt16(1), (byte[])reader[2], (byte[])reader[3], reader.GetGuid(4), reader.GetByte(5),
            reader.GetString(6), (byte[])reader[7], GetNullableString(reader, 8), GetNullableBytes(reader, 9),
            GetNullableString(reader, 10), GetNullableBytes(reader, 11), GetNullableDateTimeOffset(reader, 12), GetNullableDateTimeOffset(reader, 13), GetNullableDateOnly(reader, 14),
            reader.GetBoolean(15), GetNullableString(reader, 16), GetNullableBytes(reader, 17), reader.GetBoolean(18), GetNullableString(reader, 19), GetNullableBytes(reader, 20),
            reader.GetBoolean(21), GetNullableString(reader, 22), GetNullableBytes(reader, 23), reader.GetBoolean(24), GetNullableString(reader, 25), GetNullableBytes(reader, 26),
            reader.GetString(27), (byte[])reader[28], reader.GetString(29), (byte[])reader[30], reader.GetByte(31), GetNullableString(reader, 32), reader.GetString(33),
            reader.IsDBNull(34) ? null : reader.GetByte(34), GetNullableString(reader, 35), new MetricInputPosition(SqlServerUInt64.Materialize(reader.GetDecimal(36))));
    }

    private static void ValidatePersistedHash(LockedProjectionRow row, byte[] lookupHash)
    {
        if (row.CodecVersion != OperationalMetricEvaluationKeyV1Codec.CodecVersion ||
            !row.Hash.AsSpan().SequenceEqual(lookupHash) ||
            !OperationalMetricEvaluationKeyV1Codec.ComputeHash(row.Binary).AsSpan().SequenceEqual(row.Hash))
        {
            throw new InvalidOperationException("Persisted operational metric projection evaluation-key identity is corrupt or unsupported.");
        }
    }

    private static void ValidateExactIdentity(LockedProjectionRow row, ProjectionWriteModel model)
    {
        if (!row.Binary.AsSpan().SequenceEqual(model.Binary))
        {
            throw new InvalidOperationException("Operational metric evaluation-key SHA-256 collision or incompatible persisted identity detected.");
        }
    }

    private static void ValidateStructuredIdentity(LockedProjectionRow row, ProjectionWriteModel model)
    {
        var key = model.Projection.Key;
        if (row.MachineId != key.MachineId.Value || row.PeriodKind != model.PeriodKind ||
            !string.Equals(row.PeriodSiteId, model.PeriodSiteId, StringComparison.Ordinal) || !BytesEqual(row.PeriodSiteOrderKey, model.PeriodSiteOrderKey) ||
            !string.Equals(row.ShiftScheduleAssignmentId, model.ShiftScheduleAssignmentId, StringComparison.Ordinal) || !BytesEqual(row.ShiftScheduleAssignmentOrderKey, model.ShiftScheduleAssignmentOrderKey) ||
            !string.Equals(row.ShiftId, model.ShiftId, StringComparison.Ordinal) || !BytesEqual(row.ShiftOrderKey, model.ShiftOrderKey) ||
            row.ShiftStartsAtUtc != model.ShiftStartsAtUtc || row.ShiftEndsAtUtc != model.ShiftEndsAtUtc || row.ProductionBusinessDate != model.ProductionBusinessDate ||
            !OptionalIdentityEqual(row.ProductionOrderPresent, row.ProductionOrderId, row.ProductionOrderOrderKey, model.ProductionOrderId, model.ProductionOrderOrderKey) ||
            !OptionalIdentityEqual(row.OperationPresent, row.OperationId, row.OperationOrderKey, model.OperationId, model.OperationOrderKey) ||
            !OptionalIdentityEqual(row.PartPresent, row.PartId, row.PartOrderKey, model.PartId, model.PartOrderKey) ||
            !OptionalIdentityEqual(row.OperatorPresent, row.OperatorId, row.OperatorOrderKey, model.OperatorId, model.OperatorOrderKey) ||
            !string.Equals(row.MetricKey, model.MetricKey, StringComparison.Ordinal) || !BytesEqual(row.MetricKeyOrderKey, model.MetricKeyOrderKey) ||
            !string.Equals(row.DefinitionVersion, model.DefinitionVersion, StringComparison.Ordinal) || !BytesEqual(row.DefinitionVersionOrderKey, model.DefinitionVersionOrderKey))
        {
            throw new InvalidOperationException("Persisted operational metric projection structured identity does not match EvaluationKeyBinary V1.");
        }
    }

    private static void ValidateReplayState(LockedProjectionRow row, ProjectionWriteModel model)
    {
        var projection = model.Projection;
        if (row.Status != (byte)projection.Status ||
            !string.Equals(row.MetricValue, model.MetricValue, StringComparison.Ordinal) ||
            !string.Equals(row.Unit, projection.Unit, StringComparison.Ordinal) ||
            row.ReasonCode != (projection.ReasonCode is null ? null : (byte)projection.ReasonCode.Value) ||
            !string.Equals(row.ReasonOperandName, projection.ReasonOperandName, StringComparison.Ordinal) ||
            row.SourceRevisionPosition != projection.SourceRevision.Position)
        {
            throw new InvalidOperationException("Operational metric projection replay does not match durable projection-row state.");
        }
    }

    private static async Task<long> InsertProjectionAsync(SqlServerOperationalMetricProjectionCommitContext context, ProjectionWriteModel model, CancellationToken cancellationToken)
    {
        await using var command = context.Connection.CreateCommand();
        command.Transaction = context.Transaction;
        command.CommandText =
            "INSERT INTO dbo.OperationalMetricProjection (OperationalMetricProjectionProcessorRowId, EvaluationKeyCodecVersion, EvaluationKeyHash, EvaluationKeyBinary, MachineId, PeriodKind, PeriodSiteId, PeriodSiteOrderKey, " +
            "ShiftScheduleAssignmentId, ShiftScheduleAssignmentOrderKey, ShiftId, ShiftOrderKey, ShiftStartsAtUtc, ShiftEndsAtUtc, ProductionBusinessDate, " +
            "ProductionOrderPresent, ProductionOrderId, ProductionOrderOrderKey, OperationPresent, OperationId, OperationOrderKey, PartPresent, PartId, PartOrderKey, OperatorPresent, OperatorId, OperatorOrderKey, " +
            "MetricKey, MetricKeyOrderKey, DefinitionVersion, DefinitionVersionOrderKey, Status, MetricValue, Unit, ReasonCode, ReasonOperandName, SourceRevisionPosition) " +
            "OUTPUT INSERTED.OperationalMetricProjectionRowId VALUES (@ProcessorRowId, @CodecVersion, @Hash, @Binary, @MachineId, @PeriodKind, @PeriodSiteId, @PeriodSiteOrderKey, " +
            "@ShiftScheduleAssignmentId, @ShiftScheduleAssignmentOrderKey, @ShiftId, @ShiftOrderKey, @ShiftStartsAtUtc, @ShiftEndsAtUtc, @ProductionBusinessDate, " +
            "@ProductionOrderPresent, @ProductionOrderId, @ProductionOrderOrderKey, @OperationPresent, @OperationId, @OperationOrderKey, @PartPresent, @PartId, @PartOrderKey, @OperatorPresent, @OperatorId, @OperatorOrderKey, " +
            "@MetricKey, @MetricKeyOrderKey, @DefinitionVersion, @DefinitionVersionOrderKey, @Status, @MetricValue, @Unit, @ReasonCode, @ReasonOperandName, @SourceRevisionPosition);";
        AddProjectionParameters(command, context.ProjectionProcessorRowId, model);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task UpdateProjectionStateAsync(SqlServerOperationalMetricProjectionCommitContext context, long rowId, ProjectionWriteModel model, CancellationToken cancellationToken)
    {
        await using var command = context.Connection.CreateCommand();
        command.Transaction = context.Transaction;
        command.CommandText =
            "UPDATE dbo.OperationalMetricProjection SET Status = @Status, MetricValue = @MetricValue, Unit = @Unit, ReasonCode = @ReasonCode, ReasonOperandName = @ReasonOperandName, SourceRevisionPosition = @SourceRevisionPosition " +
            "WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId AND OperationalMetricProjectionRowId = @ProjectionRowId;";
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = context.ProjectionProcessorRowId;
        command.Parameters.Add("@ProjectionRowId", SqlDbType.BigInt).Value = rowId;
        AddStateParameters(command, model);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Operational metric projection row disappeared during replacement.");
        }
    }

    private static void AddProjectionParameters(SqlCommand command, long processorRowId, ProjectionWriteModel model)
    {
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
        command.Parameters.Add("@CodecVersion", SqlDbType.SmallInt).Value = OperationalMetricEvaluationKeyV1Codec.CodecVersion;
        command.Parameters.Add("@Hash", SqlDbType.Binary, 32).Value = model.Hash;
        command.Parameters.Add("@Binary", SqlDbType.VarBinary, -1).Value = model.Binary;
        command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value = model.Projection.Key.MachineId.Value;
        command.Parameters.Add("@PeriodKind", SqlDbType.TinyInt).Value = model.PeriodKind;
        AddString(command, "@PeriodSiteId", 256, model.PeriodSiteId);
        AddBytes(command, "@PeriodSiteOrderKey", model.PeriodSiteOrderKey);
        AddNullableString(command, "@ShiftScheduleAssignmentId", 256, model.ShiftScheduleAssignmentId);
        AddNullableBytes(command, "@ShiftScheduleAssignmentOrderKey", model.ShiftScheduleAssignmentOrderKey);
        AddNullableString(command, "@ShiftId", 256, model.ShiftId);
        AddNullableBytes(command, "@ShiftOrderKey", model.ShiftOrderKey);
        AddNullableDateTimeOffset(command, "@ShiftStartsAtUtc", model.ShiftStartsAtUtc);
        AddNullableDateTimeOffset(command, "@ShiftEndsAtUtc", model.ShiftEndsAtUtc);
        AddNullableDateOnly(command, "@ProductionBusinessDate", model.ProductionBusinessDate);
        AddOptionalIdentity(command, "ProductionOrder", model.ProductionOrderId, model.ProductionOrderOrderKey);
        AddOptionalIdentity(command, "Operation", model.OperationId, model.OperationOrderKey);
        AddOptionalIdentity(command, "Part", model.PartId, model.PartOrderKey);
        AddOptionalIdentity(command, "Operator", model.OperatorId, model.OperatorOrderKey);
        AddString(command, "@MetricKey", 256, model.MetricKey);
        AddBytes(command, "@MetricKeyOrderKey", model.MetricKeyOrderKey);
        AddString(command, "@DefinitionVersion", 256, model.DefinitionVersion);
        AddBytes(command, "@DefinitionVersionOrderKey", model.DefinitionVersionOrderKey);
        AddStateParameters(command, model);
    }

    private static void AddStateParameters(SqlCommand command, ProjectionWriteModel model)
    {
        var projection = model.Projection;
        command.Parameters.Add("@Status", SqlDbType.TinyInt).Value = (byte)projection.Status;
        AddNullableString(command, "@MetricValue", 64, model.MetricValue);
        AddString(command, "@Unit", 128, projection.Unit);
        var reasonCode = command.Parameters.Add("@ReasonCode", SqlDbType.TinyInt);
        reasonCode.Value = projection.ReasonCode is null ? DBNull.Value : (byte)projection.ReasonCode.Value;
        AddNullableString(command, "@ReasonOperandName", 256, projection.ReasonOperandName);
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@SourceRevisionPosition", projection.SourceRevision.Position));
    }

    private static void AddOptionalIdentity(SqlCommand command, string prefix, string? value, byte[]? orderKey)
    {
        command.Parameters.Add("@" + prefix + "Present", SqlDbType.Bit).Value = value is not null;
        AddNullableString(command, "@" + prefix + "Id", 256, value);
        AddNullableBytes(command, "@" + prefix + "OrderKey", orderKey);
    }

    private static void AddString(SqlCommand command, string name, int size, string value) => command.Parameters.Add(name, SqlDbType.NVarChar, size).Value = value;
    private static void AddNullableString(SqlCommand command, string name, int size, string? value) => command.Parameters.Add(name, SqlDbType.NVarChar, size).Value = value is null ? DBNull.Value : value;
    private static void AddBytes(SqlCommand command, string name, byte[] value) => command.Parameters.Add(name, SqlDbType.VarBinary, StringOrderKeyV2Codec.MaximumEncodedLength).Value = value;
    private static void AddNullableBytes(SqlCommand command, string name, byte[]? value) => command.Parameters.Add(name, SqlDbType.VarBinary, StringOrderKeyV2Codec.MaximumEncodedLength).Value = value is null ? DBNull.Value : value;
    private static void AddNullableDateTimeOffset(SqlCommand command, string name, DateTimeOffset? value) => command.Parameters.Add(name, SqlDbType.DateTimeOffset).Value = value is null ? DBNull.Value : value.Value;
    private static void AddNullableDateOnly(SqlCommand command, string name, DateOnly? value) => command.Parameters.Add(name, SqlDbType.Date).Value = value is null ? DBNull.Value : value.Value;

    private static string? GetNullableString(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static byte[]? GetNullableBytes(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : (byte[])reader[ordinal];
    private static DateTimeOffset? GetNullableDateTimeOffset(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDateTimeOffset(ordinal);
    private static DateOnly? GetNullableDateOnly(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : DateOnly.FromDateTime(reader.GetDateTime(ordinal));
    private static bool BytesEqual(byte[]? left, byte[]? right) => left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);
    private static bool OptionalIdentityEqual(bool present, string? persisted, byte[]? persistedOrderKey, string? expected, byte[]? expectedOrderKey) =>
        present == (expected is not null) && string.Equals(persisted, expected, StringComparison.Ordinal) && BytesEqual(persistedOrderKey, expectedOrderKey);

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static ByteArrayComparer Instance { get; } = new();
        public int Compare(byte[]? x, byte[]? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            return x.AsSpan().SequenceCompareTo(y);
        }
    }

    private sealed record ProjectionWriteModel(
        OperationalMetricProjection Projection, byte[] Binary, byte[] Hash, byte PeriodKind, string PeriodSiteId, byte[] PeriodSiteOrderKey,
        string? ShiftScheduleAssignmentId, byte[]? ShiftScheduleAssignmentOrderKey, string? ShiftId, byte[]? ShiftOrderKey,
        DateTimeOffset? ShiftStartsAtUtc, DateTimeOffset? ShiftEndsAtUtc, DateOnly? ProductionBusinessDate,
        string? ProductionOrderId, byte[]? ProductionOrderOrderKey, string? OperationId, byte[]? OperationOrderKey,
        string? PartId, byte[]? PartOrderOrderKey, string? OperatorId, byte[]? OperatorOrderKey,
        string MetricKey, byte[] MetricKeyOrderKey, string DefinitionVersion, byte[] DefinitionVersionOrderKey, string? MetricValue)
    {
        public byte[]? PartOrderKey => PartOrderOrderKey;
    }

    private sealed record LockedProjectionRow(
        long RowId, short CodecVersion, byte[] Hash, byte[] Binary, Guid MachineId, byte PeriodKind, string PeriodSiteId, byte[] PeriodSiteOrderKey,
        string? ShiftScheduleAssignmentId, byte[]? ShiftScheduleAssignmentOrderKey, string? ShiftId, byte[]? ShiftOrderKey,
        DateTimeOffset? ShiftStartsAtUtc, DateTimeOffset? ShiftEndsAtUtc, DateOnly? ProductionBusinessDate,
        bool ProductionOrderPresent, string? ProductionOrderId, byte[]? ProductionOrderOrderKey, bool OperationPresent, string? OperationId, byte[]? OperationOrderKey,
        bool PartPresent, string? PartId, byte[]? PartOrderKey, bool OperatorPresent, string? OperatorId, byte[]? OperatorOrderKey,
        string MetricKey, byte[] MetricKeyOrderKey, string DefinitionVersion, byte[] DefinitionVersionOrderKey,
        byte Status, string? MetricValue, string Unit, byte? ReasonCode, string? ReasonOperandName, MetricInputPosition SourceRevisionPosition);
}
