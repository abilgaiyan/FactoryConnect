using System.Data;
using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed record SqlServerOperationalMetricProjectionWriteModel(
    OperationalMetricProjection Projection,
    byte[] EvaluationKeyBinary,
    byte[] EvaluationKeyHash,
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
    string? ProductionOrderId,
    byte[]? ProductionOrderOrderKey,
    string? OperationId,
    byte[]? OperationOrderKey,
    string? PartId,
    byte[]? PartOrderKey,
    string? OperatorId,
    byte[]? OperatorOrderKey,
    string MetricKey,
    byte[] MetricKeyOrderKey,
    string DefinitionVersion,
    byte[] DefinitionVersionOrderKey,
    string? MetricValue);

internal sealed record SqlServerOperationalMetricProjectionPreparedRow(
    long? ExistingProjectionRowId,
    SqlServerOperationalMetricProjectionWriteModel WriteModel)
{
    public OperationalMetricProjection Projection => WriteModel.Projection;

    public byte[] EvaluationKeyHash => WriteModel.EvaluationKeyHash;

    public byte[] EvaluationKeyBinary => WriteModel.EvaluationKeyBinary;
}

internal sealed record SqlServerOperationalMetricProjectionMutationPlan(
    IReadOnlyList<SqlServerOperationalMetricProjectionPreparedRow> ProposedRows,
    IReadOnlyList<long> ObsoleteProjectionRowIds,
    IReadOnlyList<byte[]> LockedHashes);

internal static class SqlServerOperationalMetricProjectionRows
{
    public static async Task<SqlServerOperationalMetricProjectionMutationPlan> PrepareAsync(
        SqlServerOperationalMetricProjectionCommitContext context,
        OperationalMetricProjectionCommit commit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(commit);

        var incoming = commit.Projections.Select(Compile).ToArray();
        var incomingByHash = ValidateIncomingIdentitySet(incoming);

        // C.2 has already acquired the processor and checkpoint serialization locks.
        // This discovery read is deliberately non-locking so it cannot pre-acquire
        // projection locks in an order different from the frozen ascending-hash order.
        // Same-processor compliant writers cannot mutate projection rows while this
        // transaction owns the checkpoint slot. The complete set is revalidated under
        // transaction locks after every affected identity slot has been acquired.
        var discoveredCurrentHashes = await DiscoverCurrentHashesWithoutLocksAsync(
            context,
            cancellationToken);

        var affectedByHex = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var hash in discoveredCurrentHashes)
        {
            affectedByHex[Convert.ToHexString(hash)] = hash;
        }

        foreach (var model in incoming)
        {
            affectedByHex[Convert.ToHexString(model.EvaluationKeyHash)] = model.EvaluationKeyHash;
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

        await ValidateCompleteCurrentSetAsync(
            context,
            existingByHash,
            cancellationToken);

        if (context.Mode == SqlServerOperationalMetricProjectionCommitMode.ReconcileProposed &&
            existingByHash.Count != incoming.Length)
        {
            throw new InvalidOperationException(
                "Operational metric projection replay does not match the durable projection-row set.");
        }

        var proposedRows = new List<SqlServerOperationalMetricProjectionPreparedRow>(incoming.Length);
        foreach (var model in incoming)
        {
            var hashKey = Convert.ToHexString(model.EvaluationKeyHash);
            if (existingByHash.TryGetValue(hashKey, out var existing))
            {
                ValidateExactIdentity(existing, model);
                ValidateStructuredIdentity(existing, model);

                if (context.Mode == SqlServerOperationalMetricProjectionCommitMode.ReconcileProposed)
                {
                    ValidateReplayState(existing, model);
                }

                proposedRows.Add(new SqlServerOperationalMetricProjectionPreparedRow(
                    existing.RowId,
                    model));
            }
            else
            {
                if (context.Mode == SqlServerOperationalMetricProjectionCommitMode.ReconcileProposed)
                {
                    throw new InvalidOperationException(
                        "Operational metric projection replay is missing a durable projection row.");
                }

                proposedRows.Add(new SqlServerOperationalMetricProjectionPreparedRow(
                    null,
                    model));
            }
        }

        var incomingHashes = incomingByHash.Keys.ToHashSet(StringComparer.Ordinal);
        var obsolete = existingByHash
            .Where(pair => !incomingHashes.Contains(pair.Key))
            .Select(pair => pair.Value.RowId)
            .OrderBy(static rowId => rowId)
            .ToArray();

        if (context.Mode == SqlServerOperationalMetricProjectionCommitMode.ReconcileProposed &&
            obsolete.Length != 0)
        {
            throw new InvalidOperationException(
                "Operational metric projection replay contains obsolete durable projection rows.");
        }

        return new SqlServerOperationalMetricProjectionMutationPlan(
            proposedRows.ToArray(),
            obsolete,
            affected.Select(static hash => hash.ToArray()).ToArray());
    }

    private static Dictionary<string, SqlServerOperationalMetricProjectionWriteModel> ValidateIncomingIdentitySet(
        IReadOnlyList<SqlServerOperationalMetricProjectionWriteModel> incoming)
    {
        var incomingByHash = new Dictionary<string, SqlServerOperationalMetricProjectionWriteModel>(StringComparer.Ordinal);
        foreach (var model in incoming)
        {
            var hashKey = Convert.ToHexString(model.EvaluationKeyHash);
            if (incomingByHash.TryGetValue(hashKey, out var prior))
            {
                if (!prior.EvaluationKeyBinary.AsSpan().SequenceEqual(model.EvaluationKeyBinary))
                {
                    throw new InvalidOperationException(
                        "Operational metric evaluation-key SHA-256 collision detected within the proposed projection set.");
                }

                throw new InvalidOperationException(
                    "Duplicate operational metric evaluation-key identity detected within the proposed projection set.");
            }

            incomingByHash.Add(hashKey, model);
        }

        return incomingByHash;
    }

    private static SqlServerOperationalMetricProjectionWriteModel Compile(
        OperationalMetricProjection projection)
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
                throw new InvalidOperationException(
                    "Unsupported operational metric period type.");
        }

        var metricValue = projection.Value is null
            ? null
            : CanonicalDecimalTextV1Codec.Serialize(projection.Value.Value);

        return new SqlServerOperationalMetricProjectionWriteModel(
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

    private static async Task<IReadOnlyList<byte[]>> DiscoverCurrentHashesWithoutLocksAsync(
        SqlServerOperationalMetricProjectionCommitContext context,
        CancellationToken cancellationToken)
    {
        await using var command = context.Connection.CreateCommand();
        command.Transaction = context.Transaction;
        command.CommandText =
            "SELECT EvaluationKeyHash " +
            "FROM dbo.OperationalMetricProjection WITH (READUNCOMMITTED, INDEX(UQ_OperationalMetricProjection_LogicalHash)) " +
            "WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId " +
            "ORDER BY EvaluationKeyHash;";
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value =
            context.ProjectionProcessorRowId;

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
            "FROM dbo.OperationalMetricProjection WITH (UPDLOCK, HOLDLOCK, INDEX(UQ_OperationalMetricProjection_LogicalHash)) " +
            "WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId AND EvaluationKeyHash = @Hash;";
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value =
            context.ProjectionProcessorRowId;
        command.Parameters.Add("@Hash", SqlDbType.Binary, 32).Value = hash;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return MaterializeLockedRow(reader);
    }

    private static async Task ValidateCompleteCurrentSetAsync(
        SqlServerOperationalMetricProjectionCommitContext context,
        IReadOnlyDictionary<string, LockedProjectionRow> lockedExistingByHash,
        CancellationToken cancellationToken)
    {
        await using var command = context.Connection.CreateCommand();
        command.Transaction = context.Transaction;
        command.CommandText =
            "SELECT EvaluationKeyHash " +
            "FROM dbo.OperationalMetricProjection WITH (UPDLOCK, HOLDLOCK, INDEX(UQ_OperationalMetricProjection_LogicalHash)) " +
            "WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId " +
            "ORDER BY EvaluationKeyHash;";
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value =
            context.ProjectionProcessorRowId;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var currentHashes = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            currentHashes.Add(Convert.ToHexString((byte[])reader[0]));
        }

        if (currentHashes.Count != lockedExistingByHash.Count ||
            currentHashes.Any(hash => !lockedExistingByHash.ContainsKey(hash)))
        {
            throw new InvalidOperationException(
                "Operational metric projection identity set changed during projection lock preparation.");
        }
    }

    private static LockedProjectionRow MaterializeLockedRow(SqlDataReader reader) =>
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

    private static void ValidatePersistedHash(
        LockedProjectionRow row,
        byte[] lookupHash)
    {
        if (row.CodecVersion != OperationalMetricEvaluationKeyV1Codec.CodecVersion ||
            !row.Hash.AsSpan().SequenceEqual(lookupHash) ||
            !OperationalMetricEvaluationKeyV1Codec
                .ComputeHash(row.Binary)
                .AsSpan()
                .SequenceEqual(row.Hash))
        {
            throw new InvalidOperationException(
                "Persisted operational metric projection evaluation-key identity is corrupt or unsupported.");
        }
    }

    private static void ValidateExactIdentity(
        LockedProjectionRow row,
        SqlServerOperationalMetricProjectionWriteModel model)
    {
        if (!row.Binary.AsSpan().SequenceEqual(model.EvaluationKeyBinary))
        {
            throw new InvalidOperationException(
                "Operational metric evaluation-key SHA-256 collision or incompatible persisted identity detected.");
        }
    }

    private static void ValidateStructuredIdentity(
        LockedProjectionRow row,
        SqlServerOperationalMetricProjectionWriteModel model)
    {
        var key = model.Projection.Key;
        if (row.MachineId != key.MachineId.Value ||
            row.PeriodKind != model.PeriodKind ||
            !string.Equals(row.PeriodSiteId, model.PeriodSiteId, StringComparison.Ordinal) ||
            !BytesEqual(row.PeriodSiteOrderKey, model.PeriodSiteOrderKey) ||
            !string.Equals(row.ShiftScheduleAssignmentId, model.ShiftScheduleAssignmentId, StringComparison.Ordinal) ||
            !BytesEqual(row.ShiftScheduleAssignmentOrderKey, model.ShiftScheduleAssignmentOrderKey) ||
            !string.Equals(row.ShiftId, model.ShiftId, StringComparison.Ordinal) ||
            !BytesEqual(row.ShiftOrderKey, model.ShiftOrderKey) ||
            row.ShiftStartsAtUtc != model.ShiftStartsAtUtc ||
            row.ShiftEndsAtUtc != model.ShiftEndsAtUtc ||
            row.ProductionBusinessDate != model.ProductionBusinessDate ||
            !OptionalIdentityEqual(
                row.ProductionOrderPresent,
                row.ProductionOrderId,
                row.ProductionOrderOrderKey,
                model.ProductionOrderId,
                model.ProductionOrderOrderKey) ||
            !OptionalIdentityEqual(
                row.OperationPresent,
                row.OperationId,
                row.OperationOrderKey,
                model.OperationId,
                model.OperationOrderKey) ||
            !OptionalIdentityEqual(
                row.PartPresent,
                row.PartId,
                row.PartOrderKey,
                model.PartId,
                model.PartOrderKey) ||
            !OptionalIdentityEqual(
                row.OperatorPresent,
                row.OperatorId,
                row.OperatorOrderKey,
                model.OperatorId,
                model.OperatorOrderKey) ||
            !string.Equals(row.MetricKey, model.MetricKey, StringComparison.Ordinal) ||
            !BytesEqual(row.MetricKeyOrderKey, model.MetricKeyOrderKey) ||
            !string.Equals(row.DefinitionVersion, model.DefinitionVersion, StringComparison.Ordinal) ||
            !BytesEqual(row.DefinitionVersionOrderKey, model.DefinitionVersionOrderKey))
        {
            throw new InvalidOperationException(
                "Persisted operational metric projection structured identity does not match EvaluationKeyBinary V1.");
        }
    }

    private static void ValidateReplayState(
        LockedProjectionRow row,
        SqlServerOperationalMetricProjectionWriteModel model)
    {
        var projection = model.Projection;
        if (row.Status != (byte)projection.Status ||
            !string.Equals(row.MetricValue, model.MetricValue, StringComparison.Ordinal) ||
            !string.Equals(row.Unit, projection.Unit, StringComparison.Ordinal) ||
            row.ReasonCode != (projection.ReasonCode is null
                ? null
                : (byte)projection.ReasonCode.Value) ||
            !string.Equals(row.ReasonOperandName, projection.ReasonOperandName, StringComparison.Ordinal) ||
            row.SourceRevisionPosition != projection.SourceRevision.Position)
        {
            throw new InvalidOperationException(
                "Operational metric projection replay does not match durable projection-row state.");
        }
    }

    private static string? GetNullableString(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static byte[]? GetNullableBytes(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : (byte[])reader[ordinal];

    private static DateTimeOffset? GetNullableDateTimeOffset(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDateTimeOffset(ordinal);

    private static DateOnly? GetNullableDateOnly(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : DateOnly.FromDateTime(reader.GetDateTime(ordinal));

    private static bool BytesEqual(byte[]? left, byte[]? right) =>
        left is null
            ? right is null
            : right is not null && left.AsSpan().SequenceEqual(right);

    private static bool OptionalIdentityEqual(
        bool present,
        string? persisted,
        byte[]? persistedOrderKey,
        string? expected,
        byte[]? expectedOrderKey) =>
        present == (expected is not null) &&
        string.Equals(persisted, expected, StringComparison.Ordinal) &&
        BytesEqual(persistedOrderKey, expectedOrderKey);

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static ByteArrayComparer Instance { get; } = new();

        public int Compare(byte[]? x, byte[]? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            return x.AsSpan().SequenceCompareTo(y);
        }
    }

    private sealed record LockedProjectionRow(
        long RowId,
        short CodecVersion,
        byte[] Hash,
        byte[] Binary,
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
        byte[]? PartOrderKey,
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
}
