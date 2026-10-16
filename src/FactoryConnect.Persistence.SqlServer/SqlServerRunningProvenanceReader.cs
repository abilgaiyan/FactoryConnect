using System.Collections.Immutable;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Core.Metrics;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>Read-only exact-cut operand provenance. Does not infer continuity, emit Established,
/// publish assessments or calculate Availability.</summary>
public sealed class SqlServerRunningProvenanceReader : IRunningProvenanceReader
{
    private readonly SqlServerMetricAggregationStore _store;
    public SqlServerRunningProvenanceReader(string connectionString) => _store = new(connectionString);
    public ValueTask<RunningProvenanceResult> ReadExactAsync(RunningProvenanceRequest request, CancellationToken cancellationToken) =>
        _store.ReadRunningProvenanceAsync(request, cancellationToken);
}

internal sealed partial class SqlServerMetricAggregationStore
{
    internal async ValueTask<RunningProvenanceResult> ReadRunningProvenanceAsync(RunningProvenanceRequest request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request); request.Validate(); token.ThrowIfCancellationRequested();
        try
        {
            await using var connection = new SqlConnection(_connectionString); await connection.OpenAsync(token);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var processor = await FindProcessorAsync(connection, transaction, request.Revision.ProcessorId, token);
            if (processor is null) return Failure(RunningEvidenceState.Missing, RunningEvidenceReason.RevisionAbsent);
            var stream = await FindStreamAsync(connection, transaction, request.Revision.StreamId, token);
            if (stream is null || stream.Value != processor.Value.StreamRowId)
                return Failure(RunningEvidenceState.Incomparable, RunningEvidenceReason.ScopeMismatch);
            if (!await RevisionExistsAsync(connection, transaction, processor.Value.RowId, request.Revision.Position, token))
                return Failure(RunningEvidenceState.Missing, RunningEvidenceReason.RevisionAbsent);
            var all = await ReadHistoricalContributionsAsync(connection, processor.Value.RowId, processor.Value.StreamRowId,
                request.Revision.Position, token, transaction);
            var relevant = all.Where(x => IsStateDurationKey(x.Fact.Key) && x.Fact.EndsAtUtc > request.Extent.StartsAtUtc &&
                x.Fact.StartsAtUtc < request.Extent.EndsAtUtc).ToArray();
            if (relevant.Any(x => x.StreamId != request.Revision.StreamId || x.Fact.MachineId != request.Revision.StreamId.MachineId ||
                x.ShiftOccurrenceId != request.Shift || x.ProductionDayId != request.ProductionDay))
                return Failure(RunningEvidenceState.Incomparable, RunningEvidenceReason.ScopeMismatch);
            if (relevant.Any(x => x.Fact.SourceEligibilityIntervalId is null || x.Fact.SourceContextualizedActivityIntervalId is null))
                return Failure(RunningEvidenceState.Corrupt, RunningEvidenceReason.PersistedEvidenceInvalid);
            var slices = ImmutableArray.CreateBuilder<RunningEvidenceSlice>();
            foreach (var group in relevant.GroupBy(x => x.Fact.SourceEligibilityIntervalId!.Value))
            {
                var eligibility = await ReadSourceAsync<ProductionTimeEligibilityInterval>(connection, transaction,
                    "ProductionTimeEligibilityOutput", group.Key.Value, token);
                var e = eligibility.Value;
                if (e.Id != group.Key || e.MachineId != request.Revision.StreamId.MachineId || e.SiteId != request.Shift.SiteId ||
                    e.ShiftId != request.Shift.ShiftId || e.ShiftScheduleAssignmentId != request.Shift.ShiftScheduleAssignmentId ||
                    e.StartsAtUtc.Offset != TimeSpan.Zero || e.EndsAtUtc.Offset != TimeSpan.Zero ||
                    e.StartsAtUtc < request.Shift.StartsAtUtc || e.EndsAtUtc > request.Shift.EndsAtUtc || e.StartsAtUtc >= e.EndsAtUtc)
                    return Failure(RunningEvidenceState.Corrupt, RunningEvidenceReason.PersistedEvidenceInvalid);
                var activity = await ReadSourceAsync<ContextualizedActivityInterval>(connection, transaction,
                    "ContextualizedActivityOutput", e.SourceContextualizedActivityIntervalId.Value, token);
                var a = activity.Value;
                if (a.Id != e.SourceContextualizedActivityIntervalId || a.SourceStreamId is null || a.SourcePosition is null || a.SourceProcessorId is null ||
                    a.MachineId != e.MachineId || a.SourceStreamId.MachineId != e.MachineId || a.CompanyId != e.CompanyId ||
                    a.SiteId != e.SiteId || a.ShiftId != e.ShiftId || a.ShiftScheduleAssignmentId != e.ShiftScheduleAssignmentId ||
                    a.ProductionLineId != e.ProductionLineId || a.ProductionContextAssignmentId != e.ProductionContextAssignmentId ||
                    a.ProductionOrderId != e.ProductionOrderId || a.OperationId != e.OperationId || a.PartId != e.PartId || a.OperatorId != e.OperatorId || a.State != e.State || !Enum.IsDefined(a.State) ||
                    a.StartsAtUtc.Offset != TimeSpan.Zero || a.EndsAtUtc.Offset != TimeSpan.Zero ||
                    a.StartsAtUtc > e.StartsAtUtc || a.EndsAtUtc < e.EndsAtUtc)
                    return Failure(RunningEvidenceState.Corrupt, RunningEvidenceReason.PersistedEvidenceInvalid);
                var history = await ReadActivityHistoryFingerprintAsync(connection, transaction, a, token);
                var expected = DurableMetricInputFactDeriver.Derive([e], []).ToDictionary(x => x.Key, StringComparer.Ordinal);
                if (group.Select(x => x.Fact.Key).Distinct(StringComparer.Ordinal).Count() != group.Count() ||
                    group.Any(x => !expected.TryGetValue(x.Fact.Key, out var fact) || fact != x.Fact))
                    return Failure(RunningEvidenceState.Corrupt, RunningEvidenceReason.PersistedEvidenceInvalid);
                var stateKey = StateKey(e.State);
                slices.Add(new(e.Id.Value, a.Id.Value,
                    e.StartsAtUtc < request.Extent.StartsAtUtc ? request.Extent.StartsAtUtc : e.StartsAtUtc,
                    e.EndsAtUtc > request.Extent.EndsAtUtc ? request.Extent.EndsAtUtc : e.EndsAtUtc,
                    e.State, stateKey is not null && group.Any(x => x.Fact.Key == stateKey),
                    a.SourceProcessorId.Value, a.SourceStreamId.StreamKey, a.SourcePosition.Value,
                    a.SourceInstanceId, a.SourceSequence, eligibility.Hash, activity.Hash, history,
                    group.Select(x => x.Position).OrderBy(x => x.Value).ToImmutableArray()));
            }
            var result = RunningProvenanceReconciler.Reconcile(request, slices);
            await transaction.CommitAsync(token);
            return result;
        }
        catch (InvalidDataException) { return Failure(RunningEvidenceState.Corrupt, RunningEvidenceReason.PersistedEvidenceInvalid); }
        catch (JsonException) { return Failure(RunningEvidenceState.Corrupt, RunningEvidenceReason.PersistedEvidenceInvalid); }
        catch (ArgumentException) { return Failure(RunningEvidenceState.Corrupt, RunningEvidenceReason.PersistedEvidenceInvalid); }

        RunningProvenanceResult Failure(RunningEvidenceState state, RunningEvidenceReason reason) => RunningProvenanceReconciler.Failure(request, state, reason);
    }

    private static async Task<string> ReadActivityHistoryFingerprintAsync(SqlConnection connection, SqlTransaction transaction,
        ContextualizedActivityInterval source, CancellationToken token)
    {
        await using var command = new SqlCommand("""
            SELECT ProjectionRevision, OutputOrdinal, MachineState, StartedAt, EndedAt, InstanceId, Sequence
            FROM dbo.MachineActivityPeriodHistory WITH (HOLDLOCK)
            WHERE MachineId = @Machine AND StreamKeyBinary = @Stream AND StateProcessorIdOrderKey = @Processor AND Position = @Position;
            """, connection, transaction);
        command.Parameters.Add("@Machine", SqlDbType.UniqueIdentifier).Value = source.MachineId.Value;
        command.Parameters.Add("@Stream", SqlDbType.VarBinary, 512).Value = OrdinalStringKeyCodec.Encode(source.SourceStreamId.StreamKey);
        command.Parameters.Add("@Processor", SqlDbType.VarBinary, 769).Value = StringOrderKeyV2Codec.Encode(source.SourceProcessorId.Value);
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@Position", source.SourcePosition.Value));
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new InvalidDataException("Source activity history is absent.");
        var revision = SqlServerUInt64.Materialize(reader.GetDecimal(0)); var ordinal = reader.GetInt32(1);
        var state = reader.GetByte(2); var start = reader.GetDateTimeOffset(3); var end = reader.GetDateTimeOffset(4);
        var instance = SqlServerUInt64.Materialize(reader.GetDecimal(5)); var sequence = SqlServerUInt64.Materialize(reader.GetDecimal(6));
        // Canonical publication requires strictly increasing activity positions, including across
        // publications. Revision/ordinal identify the retained row and enter its fingerprint;
        // a second row at one position violates that publication contract, rather than choosing one.
        if (ordinal < 0 || state != (byte)source.State || start > source.StartsAtUtc || end < source.EndsAtUtc ||
            instance != source.SourceInstanceId || sequence != source.SourceSequence || await reader.ReadAsync(token))
            throw new InvalidDataException("Source activity history is ambiguous or inconsistent.");
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
        { writer.Write(revision); writer.Write(ordinal); writer.Write(state); writer.Write(start.UtcTicks); writer.Write(end.UtcTicks); writer.Write(instance); writer.Write(sequence); }
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }

    private static bool IsStateDurationKey(string key) => key is MetricInputFactKeys.ScheduledDuration or MetricInputFactKeys.PlannedProductionDuration
        or MetricInputFactKeys.RunningDuration or MetricInputFactKeys.IdleDuration or MetricInputFactKeys.StoppedDuration
        or MetricInputFactKeys.AlarmDuration or MetricInputFactKeys.OfflineDuration;
    private static string? StateKey(MachineState state) => state switch
    {
        MachineState.Running => MetricInputFactKeys.RunningDuration, MachineState.Idle => MetricInputFactKeys.IdleDuration,
        MachineState.Stopped => MetricInputFactKeys.StoppedDuration, MachineState.Fault => MetricInputFactKeys.AlarmDuration,
        MachineState.Offline => MetricInputFactKeys.OfflineDuration, _ => null,
    };
    private static async Task<(T Value, string Hash)> ReadSourceAsync<T>(SqlConnection connection, SqlTransaction transaction,
        string table, string identity, CancellationToken token) where T : class
    {
        var bytes = OrdinalStringKeyCodec.Encode(identity); var hash = SHA256.HashData(bytes);
        await using var command = new SqlCommand($"SELECT IdentityBinary, IdentityText, PayloadHash, Payload FROM dbo.{table} WITH (HOLDLOCK) WHERE IdentityHash = @Key;", connection, transaction);
        command.Parameters.Add("@Key", SqlDbType.Binary, 32).Value = hash;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new InvalidDataException("Referenced source output is missing.");
        var payload = reader.GetString(3); var contentHash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        if (!((byte[])reader.GetValue(0)).AsSpan().SequenceEqual(bytes) || reader.GetString(1) != identity ||
            !((byte[])reader.GetValue(2)).AsSpan().SequenceEqual(contentHash)) throw new InvalidDataException("Source output hash or identity mismatch.");
        return (JsonSerializer.Deserialize<T>(payload) ?? throw new InvalidDataException("Source payload is null."), Convert.ToHexString(contentHash));
    }
}
