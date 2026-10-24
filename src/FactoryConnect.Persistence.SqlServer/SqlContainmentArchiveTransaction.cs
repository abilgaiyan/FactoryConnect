using System.Data;
using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal enum ContainmentArchiveStage { WitnessesInserted, ResultsInserted, ReferencesInserted }
internal sealed record ContainmentArchiveReceipt(ReadOnlyMemory<byte> ResultId, OperationalMetricEvaluationKey Key);

/// <summary>
/// P4 archive substep only. Caller owns registration, complete retained-publication lock prefix,
/// transaction rollback/commit, retained rows/evidence/manifest and checkpoint advancement.
/// Never call this substep as a standalone production publisher.
/// </summary>
internal static class SqlContainmentArchiveTransaction
{
    private sealed record Binding(long Processor, long Aggregation, long Stream);
    private sealed record WitnessRow(short Codec, byte[] Payload, long Aggregation, long Stream, ulong Revision, long Fact, ulong Position);
    private sealed record ResultRow(byte[] Id, long Processor, short Format, long Aggregation, long Stream, ulong Revision,
        byte[] KeyHash, byte[] Key, string Policy, short Codec, byte[] Payload, byte Status, byte? Reason, byte[]? Witness);

    internal static async Task<IReadOnlyList<ContainmentArchiveReceipt>> PrepareAsync(SqlConnection connection,
        SqlTransaction transaction, IReadOnlyList<ReadOnlyMemory<byte>> envelopes, bool requireExisting,
        CancellationToken cancellationToken, Func<ContainmentArchiveStage, CancellationToken, Task>? failureInjection = null, bool verifyOnly = false,
        SqlContainmentHistoricalReadScope? historicalReads = null)
    {
        if (verifyOnly && !requireExisting) throw new ArgumentException("Read-only verification requires all archives to exist.", nameof(requireExisting));
        var batch = ContainmentArchiveBatch.Preflight(envelopes);
        SqlContainmentHistoricalContributionReader.RequireStableTransaction(connection, transaction);
        historicalReads ??= new SqlContainmentHistoricalReadScope(connection, transaction);
        historicalReads.RequireOwner(connection, transaction);
        cancellationToken.ThrowIfCancellationRequested();
        await SqlServerOperationalMetricProjectionProcessorGate.AcquireAsync(connection, transaction, batch.Processor,
            verifyOnly ? OperationalMetricProjectionProcessorGateMode.Shared : OperationalMetricProjectionProcessorGateMode.Exclusive, cancellationToken);
        var binding = await ReadBinding(connection, transaction, batch, verifyOnly, cancellationToken);
        // Caller holds the retained prefix first; these archive locks continue in reviewed deterministic order.
        var existing = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var result in batch.Results)
        {
            await using var command = CreateForMode(ResultSelect +
                " WITH (UPDLOCK,HOLDLOCK,INDEX(UQ_OMPRA_Slot)) WHERE OperationalMetricProjectionProcessorRowId=@P AND EvaluationKeyHash=@K AND SourceRevisionPosition=@A;");
            command.Parameters.AddWithValue("@P", binding.Processor);
            Bytes(command, "@K", result.KeyHash, 32);
            command.Parameters.Add(SqlServerUInt64.CreateParameter("@A", batch.Cut.Position.Value));
            var row = await ReadResult(command, cancellationToken);
            if (row is not null) MatchResult(row, result, binding, batch);
            else if (requireExisting) throw Integrity("Replay cannot repair a missing result archive.");
            existing.Add(Convert.ToHexString(result.Id), row is not null);
        }
        var witnessRows = new Dictionary<string, WitnessRow?>(StringComparer.Ordinal);
        foreach (var witness in batch.Witnesses)
        {
            await using var command = CreateForMode("""
                SELECT WitnessCodecVersion,WitnessPayload,MetricAggregationProcessorRowId,MetricInputStreamRowId,
                       SourceRevisionPosition,ContributionFactRowId,ContributionPosition
                FROM dbo.OperationalMetricConflictWitnessArchive WITH (UPDLOCK,HOLDLOCK)
                WHERE WitnessId=@Id;
                """);
            Bytes(command, "@Id", witness.Id, 32);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            witnessRows.Add(Convert.ToHexString(witness.Id), await reader.ReadAsync(cancellationToken)
                ? new(reader.GetInt16(0), (byte[])reader[1], reader.GetInt64(2), reader.GetInt64(3),
                    SqlServerUInt64.Materialize(reader.GetDecimal(4)), reader.GetInt64(5), SqlServerUInt64.Materialize(reader.GetDecimal(6))) : null);
        }
        foreach (var result in batch.Results.OrderBy(r => Convert.ToHexString(r.Id), StringComparer.Ordinal))
        {
            await using var command = CreateForMode(ResultSelect + " WITH (UPDLOCK,HOLDLOCK) WHERE ResultId=@Id;");
            Bytes(command, "@Id", result.Id, 32);
            var row = await ReadResult(command, cancellationToken);
            if (row is not null) MatchResult(row, result, binding, batch);
            if ((row is not null) != existing[Convert.ToHexString(result.Id)]) throw Integrity("Result slot and digest disagree.");
        }
        foreach (var result in batch.Results.OrderBy(r => Convert.ToHexString(r.Id), StringComparer.Ordinal))
        {
            await using var command = CreateForMode(
                "SELECT NodePath,WitnessId FROM dbo.OperationalMetricResultWitnessReference WITH (UPDLOCK,HOLDLOCK) WHERE ResultId=@Id ORDER BY NodePath;");
            Bytes(command, "@Id", result.Id, 32);
            var references = new List<ContainmentArchiveBatch.Reference>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) references.Add(new((byte[])reader[0], (byte[])reader[1]));
            if (existing[Convert.ToHexString(result.Id)])
            {
                if (references.Count != result.References.Count || references.Where((r, i) =>
                        !r.Path.AsSpan().SequenceEqual(result.References[i].Path) || !r.WitnessId.AsSpan().SequenceEqual(result.References[i].WitnessId)).Any())
                    throw Integrity("Replay has a missing, extra or mismatched witness reference.");
                if (result.References.Any(r => witnessRows[Convert.ToHexString(r.WitnessId)] is null))
                    throw Integrity("Replay cannot repair a missing witness archive.");
            }
            else if (references.Count != 0) throw Integrity("A new result has orphan witness references.");
        }
        // No archive DML is allowed until every incoming result passes P3 in this exact transaction.
        foreach (var result in batch.Results)
            _ = await SqlContainmentHistoricalResultValidator.VerifyAsync(connection, transaction, result.Payload, cancellationToken, historicalReads);
        var factRows = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var witness in batch.Witnesses)
        {
            var hex = Convert.ToHexString(witness.Id);
            await using var fact = CreateForMode("""
                SELECT f.MetricInputFactRowId,f.FactId
                FROM dbo.MetricAggregationContribution c
                JOIN dbo.MetricInputFact f ON f.MetricInputFactRowId=c.MetricInputFactRowId
                  AND f.MetricInputStreamRowId=c.MetricInputStreamRowId AND f.Position=c.Position
                WHERE c.MetricAggregationProcessorRowId=@AP AND c.MetricInputStreamRowId=@S
                  AND c.Position=@Position AND f.FactIdBinary=@FactKey;
                """);
            fact.Parameters.AddWithValue("@AP", binding.Aggregation); fact.Parameters.AddWithValue("@S", binding.Stream);
            fact.Parameters.Add(SqlServerUInt64.CreateParameter("@Position", witness.Document.Contribution.Position.Value));
            Bytes(fact, "@FactKey", OrdinalStringKeyCodec.Encode(witness.Document.Contribution.Fact.Id.Value), 512);
            long factRow;
            await using (var reader = await fact.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken) || reader.GetString(1) != witness.Document.Contribution.Fact.Id.Value)
                    throw Integrity("Verified witness contribution is absent from the exact source ledger.");
                factRow = reader.GetInt64(0);
            }
            factRows.Add(hex, factRow);
            if (witnessRows[hex] is { } row && (row.Codec != 1 || !row.Payload.AsSpan().SequenceEqual(witness.Payload) ||
                row.Aggregation != binding.Aggregation || row.Stream != binding.Stream || row.Revision != batch.Cut.Position.Value ||
                row.Fact != factRow || row.Position != witness.Document.Contribution.Position.Value))
                throw Integrity("Witness digest or complete archive index conflicts with canonical source evidence.");
            if (requireExisting && witnessRows[hex] is null) throw Integrity("Replay cannot insert a missing witness.");
        }
        if (verifyOnly)
            return batch.Results.Select(r => new ContainmentArchiveReceipt(r.Id.ToArray(), r.Document.Key)).ToArray();
        foreach (var witness in batch.Witnesses.Where(w => witnessRows[Convert.ToHexString(w.Id)] is null))
        {
            await using var command = CreateForMode("""
                INSERT dbo.OperationalMetricConflictWitnessArchive
                (WitnessId,WitnessCodecVersion,WitnessPayload,MetricAggregationProcessorRowId,MetricInputStreamRowId,
                 SourceRevisionPosition,ContributionFactRowId,ContributionPosition)
                VALUES (@Id,1,@Payload,@AP,@S,@A,@Fact,@Position);
                """);
            Bytes(command, "@Id", witness.Id, 32); Bytes(command, "@Payload", witness.Payload);
            command.Parameters.AddWithValue("@AP", binding.Aggregation); command.Parameters.AddWithValue("@S", binding.Stream);
            command.Parameters.AddWithValue("@Fact", factRows[Convert.ToHexString(witness.Id)]);
            command.Parameters.Add(SqlServerUInt64.CreateParameter("@A", batch.Cut.Position.Value));
            command.Parameters.Add(SqlServerUInt64.CreateParameter("@Position", witness.Document.Contribution.Position.Value));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await Inject(ContainmentArchiveStage.WitnessesInserted);
        foreach (var result in batch.Results.Where(r => !existing[Convert.ToHexString(r.Id)]))
        {
            await using var command = CreateForMode("""
                INSERT dbo.OperationalMetricPublicationResultArchive
                (ResultId,OperationalMetricProjectionProcessorRowId,PublicationFormatVersion,MetricAggregationProcessorRowId,
                 MetricInputStreamRowId,SourceRevisionPosition,EvaluationKeyHash,EvaluationKeyBinary,EvaluationPolicyId,
                 ResultCodecVersion,ResultPayload,Status,ReasonCode,ConflictWitnessId)
                VALUES (@Id,@P,2,@AP,@S,@A,@Hash,@Key,@Policy,2,@Payload,@Status,@Reason,@Witness);
                """);
            Bytes(command, "@Id", result.Id, 32); Bytes(command, "@Hash", result.KeyHash, 32); Bytes(command, "@Key", result.Key);
            Bytes(command, "@Payload", result.Payload);
            command.Parameters.AddWithValue("@P", binding.Processor); command.Parameters.AddWithValue("@AP", binding.Aggregation);
            command.Parameters.AddWithValue("@S", binding.Stream); command.Parameters.Add(SqlServerUInt64.CreateParameter("@A", batch.Cut.Position.Value));
            command.Parameters.Add("@Policy", SqlDbType.NVarChar, 64).Value = AvailabilityContainmentV1.PolicyId;
            command.Parameters.Add("@Status", SqlDbType.TinyInt).Value = (byte)result.Document.Status;
            command.Parameters.Add("@Reason", SqlDbType.TinyInt).Value = result.Document.ReasonCode is { } reason ? (byte)reason : DBNull.Value;
            command.Parameters.Add("@Witness", SqlDbType.Binary, 32).Value = result.Document.Witness is null ? DBNull.Value :
                OperationalMetricContainmentIdentity.ComputeWitnessId(OperationalMetricConflictWitnessV1Codec.Encode(result.Document.Witness));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await Inject(ContainmentArchiveStage.ResultsInserted);
        foreach (var result in batch.Results.Where(r => !existing[Convert.ToHexString(r.Id)]).OrderBy(r => Convert.ToHexString(r.Id), StringComparer.Ordinal))
        foreach (var reference in result.References)
        {
            await using var command = CreateForMode(
                "INSERT dbo.OperationalMetricResultWitnessReference (ResultId,NodePath,WitnessId) VALUES (@Result,@Path,@Witness);");
            Bytes(command, "@Result", result.Id, 32); Bytes(command, "@Path", reference.Path, 256); Bytes(command, "@Witness", reference.WitnessId, 32);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await Inject(ContainmentArchiveStage.ReferencesInserted);
        return batch.Results.Select(r => new ContainmentArchiveReceipt(r.Id.ToArray(), r.Document.Key)).ToArray();

        SqlCommand CreateForMode(string sql) => Create(connection, transaction, sql, verifyOnly);

        async Task Inject(ContainmentArchiveStage stage)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Identical replay executes no DML and does not traverse mutation failure hooks.
            if (failureInjection is not null && existing.Values.Any(v => !v)) await failureInjection(stage, cancellationToken);
        }
    }

    private static async Task<Binding> ReadBinding(SqlConnection connection, SqlTransaction transaction,
        ContainmentArchiveBatch batch, bool verifyOnly, CancellationToken cancellationToken)
    {
        await using var command = Create(connection, transaction, """
            SELECT p.OperationalMetricProjectionProcessorRowId,p.MetricAggregationProcessorRowId,p.MetricInputStreamRowId,
                   p.ProcessorKey,p.PublicationFormatVersion,p.EvaluationPolicyId,
                   ap.ProcessorKey,ap.ProcessorKeyBinary,ap.MetricInputStreamRowId,s.MachineId,s.StreamKey,s.StreamKeyBinary
            FROM dbo.OperationalMetricProjectionProcessor p WITH (UPDLOCK,HOLDLOCK)
            JOIN dbo.MetricAggregationProcessor ap ON ap.MetricAggregationProcessorRowId=p.MetricAggregationProcessorRowId
            JOIN dbo.MetricInputStream s ON s.MetricInputStreamRowId=p.MetricInputStreamRowId
            WHERE p.ProcessorKeyBinary=@Key;
            """, verifyOnly);
        Bytes(command, "@Key", StringOrderKeyV2Codec.Encode(batch.Processor.Value), StringOrderKeyV2Codec.MaximumEncodedLength);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw Integrity("Containment archive requires a registered format-2 processor.");
        if (reader.GetString(3) != batch.Processor.Value || reader.GetInt16(4) != 2 || reader.IsDBNull(5) || reader.GetString(5) != AvailabilityContainmentV1.PolicyId ||
            reader.GetString(6) != batch.Cut.ProcessorId.Value || !((byte[])reader[7]).AsSpan().SequenceEqual(OrdinalStringKeyCodec.Encode(batch.Cut.ProcessorId.Value)) ||
            reader.GetInt64(8) != reader.GetInt64(2) || reader.GetGuid(9) != batch.Cut.StreamId.MachineId.Value ||
            reader.GetString(10) != batch.Cut.StreamId.StreamKey || !((byte[])reader[11]).AsSpan().SequenceEqual(OrdinalStringKeyCodec.Encode(batch.Cut.StreamId.StreamKey)))
            throw Integrity("Containment processor format/policy/source registration contradicts the exact batch.");
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private const string ResultSelect = """
        SELECT ResultId,OperationalMetricProjectionProcessorRowId,PublicationFormatVersion,MetricAggregationProcessorRowId,
               MetricInputStreamRowId,SourceRevisionPosition,EvaluationKeyHash,EvaluationKeyBinary,EvaluationPolicyId,
               ResultCodecVersion,ResultPayload,Status,ReasonCode,ConflictWitnessId
        FROM dbo.OperationalMetricPublicationResultArchive
        """;
    private static async Task<ResultRow?> ReadResult(SqlCommand command, CancellationToken token)
    {
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? new((byte[])reader[0], reader.GetInt64(1), reader.GetInt16(2), reader.GetInt64(3),
            reader.GetInt64(4), SqlServerUInt64.Materialize(reader.GetDecimal(5)), (byte[])reader[6], (byte[])reader[7],
            reader.GetString(8), reader.GetInt16(9), (byte[])reader[10], reader.GetByte(11), reader.IsDBNull(12) ? null : reader.GetByte(12),
            reader.IsDBNull(13) ? null : (byte[])reader[13]) : null;
    }
    private static void MatchResult(ResultRow row, ContainmentArchiveBatch.Result expected, Binding binding, ContainmentArchiveBatch batch)
    {
        // Digest matches never replace complete canonical/index equality.
        if (!OperationalMetricContainmentIdentity.ComputeResultId(row.Payload).AsSpan().SequenceEqual(row.Id) ||
            !OperationalMetricEvaluationKeyV1Codec.ComputeHash(row.Key).AsSpan().SequenceEqual(row.KeyHash))
            throw Integrity("Stored result or key digest is corrupt.");
        var stored = OperationalMetricDependencySnapshotV2Codec.Decode(row.Payload);
        var storedKey = OperationalMetricEvaluationKeyV1Codec.Encode(stored.Key);
        var root = stored.Witness is null ? null : OperationalMetricContainmentIdentity.ComputeWitnessId(OperationalMetricConflictWitnessV1Codec.Encode(stored.Witness));
        if (row.Format != 2 || row.Codec != 2 || row.Policy != AvailabilityContainmentV1.PolicyId ||
            row.Processor != binding.Processor || row.Aggregation != binding.Aggregation || row.Stream != binding.Stream ||
            stored.ProcessorId != batch.Processor || stored.SourceRevision != batch.Cut || row.Revision != stored.SourceRevision.Position.Value ||
            !row.Key.AsSpan().SequenceEqual(storedKey) || row.Status != (byte)stored.Status || row.Reason != (stored.ReasonCode is { } reason ? (byte?)reason : null) ||
            !Equal(row.Witness, root)) throw Integrity("Stored result archive index contradicts its canonical payload.");
        if (!row.Id.AsSpan().SequenceEqual(expected.Id) || !row.Key.AsSpan().SequenceEqual(expected.Key) ||
            !row.Payload.AsSpan().SequenceEqual(expected.Payload))
            throw new ContainmentPublicationConflictException("A result slot/digest is already bound to different canonical content.");
    }
    private static bool Equal(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);
    private static ContainmentHistoricalIntegrityException Integrity(string message) => new(message);
    private static SqlCommand Create(SqlConnection connection, SqlTransaction transaction, string sql, bool verifyOnly = false)
    { var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = verifyOnly ? sql.Replace("UPDLOCK,HOLDLOCK", "HOLDLOCK", StringComparison.Ordinal) : sql; return command; }
    private static void Bytes(SqlCommand command, string name, byte[] bytes, int size = -1) => command.Parameters.Add(name,
        size == 32 ? SqlDbType.Binary : SqlDbType.VarBinary, size).Value = bytes;
}
