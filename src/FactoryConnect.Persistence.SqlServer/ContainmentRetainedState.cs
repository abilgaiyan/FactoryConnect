using System.Data;
using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

// SQL rendering of canonical claims. These maps are never authority: publication requires P3 first.
internal static class ContainmentRetainedState
{
    internal sealed class Row : Dictionary<string, (SqlDbType Type, object? Value)>
    {
        internal void Put(string column, SqlDbType type, object? value) => Add(column, (type, value));
        internal void Match(IReadOnlyDictionary<string, object?> observed)
        {
            foreach (var (name, field) in this)
            {
                if (!observed.TryGetValue(name, out var actual) || !Equal(field.Value, actual))
                    throw new ContainmentHistoricalIntegrityException($"Retained publication field {name} contradicts its canonical archive.");
            }
        }
    }
    internal static bool Equal(object? a, object? b) => a is byte[] x ? b is byte[] y && x.AsSpan().SequenceEqual(y) : Equals(a, b);
    internal static Row Projection(long processor, ContainmentArchiveBatch.Result result)
    {
        var d = result.Document; var key = d.Key; var row = new Row();
        row.Put("OperationalMetricProjectionProcessorRowId", SqlDbType.BigInt, processor);
        row.Put("EvaluationKeyCodecVersion", SqlDbType.SmallInt, (short)1);
        row.Put("EvaluationKeyHash", SqlDbType.Binary, result.KeyHash);
        row.Put("EvaluationKeyBinary", SqlDbType.VarBinary, result.Key);
        row.Put("MachineId", SqlDbType.UniqueIdentifier, key.MachineId.Value);
        row.Put("MachineOrderKey", SqlDbType.Binary, Convert.FromHexString(key.MachineId.Value.ToString("N")));
        var shift = key.PeriodId as OperationalMetricPeriodId.Shift;
        var day = key.PeriodId as OperationalMetricPeriodId.ProductionDay;
        row.Put("PeriodKind", SqlDbType.TinyInt, shift is null ? (byte)2 : (byte)1);
        TextIdentity(row, "PeriodSiteId", "PeriodSiteOrderKey", shift?.ShiftOccurrenceId.SiteId.Value ?? day!.ProductionDayId.SiteId.Value);
        TextIdentity(row, "ShiftScheduleAssignmentId", "ShiftScheduleAssignmentOrderKey", shift?.ShiftOccurrenceId.ShiftScheduleAssignmentId.Value);
        TextIdentity(row, "ShiftId", "ShiftOrderKey", shift?.ShiftOccurrenceId.ShiftId.Value);
        row.Put("ShiftStartsAtUtc", SqlDbType.DateTimeOffset, shift?.ShiftOccurrenceId.StartsAtUtc);
        row.Put("ShiftEndsAtUtc", SqlDbType.DateTimeOffset, shift?.ShiftOccurrenceId.EndsAtUtc);
        row.Put("ProductionBusinessDate", SqlDbType.Date, day is null ? null : day.ProductionDayId.BusinessDate.ToDateTime(TimeOnly.MinValue));
        Optional("ProductionOrder", key.ContextKey.ProductionOrderId?.Value);
        Optional("Operation", key.ContextKey.OperationId?.Value);
        Optional("Part", key.ContextKey.PartId?.Value);
        Optional("Operator", key.ContextKey.OperatorId?.Value);
        TextIdentity(row, "MetricKey", "MetricKeyOrderKey", key.DefinitionId.MetricKey);
        TextIdentity(row, "DefinitionVersion", "DefinitionVersionOrderKey", key.DefinitionId.Version);
        row.Put("Status", SqlDbType.TinyInt, (byte)d.Status);
        row.Put("MetricValue", SqlDbType.NVarChar, d.Value is { } value ? CanonicalDecimalTextV1Codec.Serialize(value) : null);
        row.Put("Unit", SqlDbType.NVarChar, d.Unit);
        row.Put("ReasonCode", SqlDbType.TinyInt, d.ReasonCode is { } reason ? (byte)reason : null);
        row.Put("ReasonOperandName", SqlDbType.NVarChar, d.ReasonOperandName);
        row.Put("SourceRevisionPosition", SqlDbType.Decimal, (decimal)d.SourceRevision.Position.Value);
        row.Put("PublicationFormatVersion", SqlDbType.SmallInt, (short)2);
        row.Put("EvaluationPolicyId", SqlDbType.NVarChar, AvailabilityContainmentV1.PolicyId);
        row.Put("ContainmentResultId", SqlDbType.Binary, result.Id);
        row.Put("ConflictWitnessId", SqlDbType.Binary, d.Witness is null ? null : OperationalMetricContainmentIdentity.ComputeWitnessId(OperationalMetricConflictWitnessV1Codec.Encode(d.Witness)));
        return row;
        void Optional(string name, string? value)
        {
            row.Put(name + "Present", SqlDbType.Bit, value is not null);
            TextIdentity(row, name + "Id", name + "OrderKey", value);
        }
    }
    private static void TextIdentity(Row row, string name, string order, string? value)
    {
        row.Put(name, SqlDbType.NVarChar, value);
        row.Put(order, SqlDbType.VarBinary, value is null ? null : StringOrderKeyV2Codec.Encode(value));
    }
    internal static Row[] Evidence(long projection, ContainmentResultDocument document)
    {
        var rows = new List<Row>();
        for (var i = 0; i < document.Components.Count; i++)
        {
            var e = document.Components[i]; var row = Base(1, i, e.OperandName);
            Set("ComponentKey", e.SourceIdentity.ComponentKey); Set("MetricDimension", (byte)e.Dimension);
            Set("ComponentValue", CanonicalDecimalTextV1Codec.Serialize(e.Value)); Set("ComponentUnit", e.Unit);
            Set("InputCount", (decimal)e.InputCount); Set("FirstInputTimestamp", e.FirstInputTimestamp); Set("LastInputTimestamp", e.LastInputTimestamp);
            rows.Add(row);
            void Set(string column, object value) => row[column] = (row[column].Type, value);
        }
        for (var i = 0; i < document.Dependencies.Count; i++)
        {
            var e = document.Dependencies[i]; var row = Base(2, i, e.OperandName);
            var payload = OperationalMetricDependencySnapshotV2Codec.Encode(e.Result);
            Set("DependencyMetricKey", e.DefinitionId.MetricKey); Set("DependencyDefinitionVersion", e.DefinitionId.Version);
            Set("DependencySnapshotCodecVersion", (short)2); Set("DependencySnapshotHash", OperationalMetricContainmentIdentity.ComputeDependencyHash(payload));
            Set("DependencySnapshotBinary", payload); rows.Add(row);
            void Set(string column, object value) => row[column] = (row[column].Type, value);
        }
        return rows.ToArray();
        Row Base(byte kind, int ordinal, string operand)
        {
            var row = new Row();
            row.Put("OperationalMetricProjectionRowId", SqlDbType.BigInt, projection);
            row.Put("EvidenceKind", SqlDbType.TinyInt, kind); row.Put("EvidenceOrdinal", SqlDbType.Int, ordinal);
            TextIdentity(row, "OperandName", "OperandNameOrderKey", operand);
            row.Put("ComponentKey", SqlDbType.NVarChar, null); row.Put("MetricDimension", SqlDbType.TinyInt, null);
            row.Put("ComponentValue", SqlDbType.NVarChar, null); row.Put("ComponentUnit", SqlDbType.NVarChar, null);
            row.Put("InputCount", SqlDbType.Decimal, null); row.Put("FirstInputTimestamp", SqlDbType.DateTimeOffset, null); row.Put("LastInputTimestamp", SqlDbType.DateTimeOffset, null);
            row.Put("DependencyMetricKey", SqlDbType.NVarChar, null); row.Put("DependencyDefinitionVersion", SqlDbType.NVarChar, null);
            row.Put("DependencySnapshotCodecVersion", SqlDbType.SmallInt, null); row.Put("DependencySnapshotHash", SqlDbType.Binary, null); row.Put("DependencySnapshotBinary", SqlDbType.VarBinary, null);
            return row;
        }
    }
    // Identifiers below originate only from the fixed maps above, never from incoming text.
    internal static void Parameters(SqlCommand command, Row row)
    {
        foreach (var (name, field) in row)
        {
            var parameter = command.Parameters.Add("@" + name, field.Type);
            if (field.Type is SqlDbType.VarBinary or SqlDbType.NVarChar) parameter.Size = -1;
            if (field.Type == SqlDbType.Binary) parameter.Size = ((byte[]?)field.Value)?.Length ?? 32;
            if (field.Type == SqlDbType.Decimal) { parameter.Precision = 20; parameter.Scale = 0; }
            parameter.Value = field.Value ?? DBNull.Value;
        }
    }
    internal static async Task<List<Dictionary<string, object?>>> Read(SqlCommand command, CancellationToken token)
    {
        var rows = new List<Dictionary<string, object?>>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++) row.Add(reader.GetName(i), reader.IsDBNull(i) ? null : reader.GetValue(i));
            rows.Add(row);
        }
        return rows;
    }
}
