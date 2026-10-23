using System.Data;
using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;
using static FactoryConnect.Persistence.SqlServer.ContainmentRetainedState;

namespace FactoryConnect.Persistence.SqlServer;

internal enum ContainmentPublicationStage
{
    Registered, WitnessesInserted, ResultsInserted, ReferencesInserted, EvidenceDeleted,
    ManifestDeleted, ProjectionsWritten, EvidenceWritten, ManifestWritten, CheckpointWritten, Revalidated
}

/// <summary>
/// Internal format-2 transaction, reached through the explicitly selected containment publisher.
/// Owns the complete transaction; the public V1 commit guard remains in force.
/// </summary>
internal sealed class SqlContainmentPublicationTransaction(string connectionString)
{
    internal async Task PublishAsync(IReadOnlyList<ReadOnlyMemory<byte>> envelopes,
        OperationalMetricProjectionCheckpoint? expected, CancellationToken token,
        Func<ContainmentPublicationStage, CancellationToken, Task>? failureInjection = null)
    {
        var batch = ContainmentArchiveBatch.Preflight(envelopes);
        // Freeze once: downstream helpers must never observe caller buffer changes.
        var frozen = batch.Results.Select(r => (ReadOnlyMemory<byte>)r.Payload).ToArray();
        if (expected is not null && (expected.ProcessorId != batch.Processor ||
            expected.SourceRevision.ProcessorId != batch.Cut.ProcessorId || expected.SourceRevision.StreamId != batch.Cut.StreamId))
            throw new ArgumentException("Expected checkpoint belongs to a different processor/source.", nameof(expected));
        await using var connection = new SqlConnection(connectionString); await connection.OpenAsync(token);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        try
        {
            // One order for every format-2 writer: gate -> registration -> checkpoint -> complete
            // projection hash prefix -> manifest -> evidence(row id) -> retained archives -> incoming
            // archive slots/witnesses/results/references -> historical authority -> DML -> checkpoint.
            await SqlServerOperationalMetricProjectionProcessorGate.AcquireAsync(connection, transaction, batch.Processor,
                OperationalMetricProjectionProcessorGateMode.Exclusive, token);
            // This scope belongs only to this Exclusive publication transaction.
            // Never reuse the preparation transaction's historical evidence here.
            var historicalReads = new SqlContainmentHistoricalReadScope(connection, transaction);
            var processor = await Register();
            var checkpoint = await ReadCheckpoint();
            var replay = checkpoint == batch.Cut.Position.Value;
            if (!replay && (checkpoint != expected?.SourceRevision.Position.Value || checkpoint > batch.Cut.Position.Value))
                throw new ContainmentPublicationConflictException("Publication checkpoint conflict or backward revision.");
            var current = await Query("SELECT * FROM dbo.OperationalMetricProjection WITH (UPDLOCK,HOLDLOCK,INDEX(UQ_OperationalMetricProjection_LogicalHash)) WHERE OperationalMetricProjectionProcessorRowId=@P ORDER BY EvaluationKeyHash;");
            var manifest = await Manifest();
            var evidence = new Dictionary<long, List<Dictionary<string, object?>>>();
            foreach (var row in current.OrderBy(r => (long)r["OperationalMetricProjectionRowId"]!))
            {
                var id = (long)row["OperationalMetricProjectionRowId"]!;
                evidence.Add(id, await Query("SELECT * FROM dbo.OperationalMetricProjectionEvidence WITH (UPDLOCK,HOLDLOCK) WHERE OperationalMetricProjectionRowId=@Id ORDER BY EvidenceKind,EvidenceOrdinal;", id));
            }
            if (checkpoint is null && (current.Count != 0 || manifest.Length != 0)) throw Integrity("Retained state exists without a checkpoint.");
            if (checkpoint is not null && current.Count == 0) throw Integrity("Checkpoint has no retained publication.");
            foreach (var row in current)
            {
                var id = (long)row["OperationalMetricProjectionRowId"]!;
                if (row["ContainmentResultId"] is not byte[] resultId) throw Integrity("Retained row has no format-2 archive binding.");
                var archives = await Query("SELECT ResultPayload FROM dbo.OperationalMetricPublicationResultArchive WHERE ResultId=@Result;", result: resultId);
                if (archives.Count != 1) throw Integrity("Retained archive is missing.");
                var payload = (byte[])archives[0]["ResultPayload"]!;
                ContainmentArchiveBatch prior;
                try { prior = ContainmentArchiveBatch.Preflight([(ReadOnlyMemory<byte>)payload]); }
                catch (InvalidDataException error) { throw new ContainmentHistoricalIntegrityException("Retained canonical archive is malformed.", error); }
                if (prior.Processor != batch.Processor || prior.Cut.ProcessorId != batch.Cut.ProcessorId || prior.Cut.StreamId != batch.Cut.StreamId ||
                    prior.Cut.Position.Value > checkpoint || !prior.Results[0].Id.AsSpan().SequenceEqual(resultId))
                    throw Integrity("Retained result identity/source is contradictory.");
                Projection(processor, prior.Results[0]).Match(row);
                MatchEvidence(Evidence(id, prior.Results[0].Document), evidence[id]);
                // Existing receipts also require complete immutable archive/reference and P3 authority.
                _ = await SqlContainmentArchiveTransaction.PrepareAsync(connection, transaction, [(ReadOnlyMemory<byte>)payload], true, token, historicalReads: historicalReads);
            }
            if (checkpoint is not null)
            {
                var latest = current.Where(r => (decimal)r["SourceRevisionPosition"]! == (decimal)checkpoint.Value)
                    .Select(r => (long)r["OperationalMetricProjectionRowId"]!).Order().ToArray();
                if (latest.Length == 0 || !latest.SequenceEqual(manifest)) throw Integrity("Latest manifest is incomplete or contradictory.");
                var latestArchives = await Query("SELECT ResultId FROM dbo.OperationalMetricPublicationResultArchive WITH (UPDLOCK,HOLDLOCK,INDEX(UQ_OMPRA_Slot)) WHERE OperationalMetricProjectionProcessorRowId=@P AND SourceRevisionPosition=@A;", through: checkpoint);
                var retainedIds = current.Where(r => manifest.Contains((long)r["OperationalMetricProjectionRowId"]!)).Select(r => Convert.ToHexString((byte[])r["ContainmentResultId"]!)).Order(StringComparer.Ordinal);
                if (!latestArchives.Select(r => Convert.ToHexString((byte[])r["ResultId"]!)).Order(StringComparer.Ordinal).SequenceEqual(retainedIds))
                    throw Integrity("Latest archive inventory differs from retained publication.");
                if (!replay)
                {
                    var expectedKeys = expected!.BatchManifest.ProjectionKeys.Select(k => Convert.ToHexString(OperationalMetricEvaluationKeyV1Codec.Encode(k))).Order(StringComparer.Ordinal);
                    var actualKeys = current.Where(r => manifest.Contains((long)r["OperationalMetricProjectionRowId"]!)).Select(r => Convert.ToHexString((byte[])r["EvaluationKeyBinary"]!)).Order(StringComparer.Ordinal);
                    if (!expectedKeys.SequenceEqual(actualKeys)) throw new ContainmentPublicationConflictException("Expected manifest differs from durable latest batch.");
                }
            }
            var byHash = current.ToDictionary(r => Convert.ToHexString((byte[])r["EvaluationKeyHash"]!), StringComparer.Ordinal);
            foreach (var result in batch.Results)
                if (byHash.TryGetValue(Convert.ToHexString(result.KeyHash), out var sameHash) && !Equal(sameHash["EvaluationKeyBinary"], result.Key))
                    throw Integrity("Evaluation-key digest collision.");
            if (replay)
            {
                var replayIds = new List<long>();
                foreach (var result in batch.Results)
                {
                    if (!byHash.TryGetValue(Convert.ToHexString(result.KeyHash), out var row)) throw Integrity("Replay cannot insert a missing retained row.");
                    Projection(processor, result).Match(row);
                    var id = (long)row["OperationalMetricProjectionRowId"]!; replayIds.Add(id);
                    MatchEvidence(Evidence(id, result.Document), evidence[id]);
                }
                if (!replayIds.Order().SequenceEqual(manifest)) throw Integrity("Replay manifest differs from complete incoming batch.");
            }
            // Reject an independently committed archive-only batch instead of completing/repairing it.
            if (!replay)
            {
                var priorSlots = await Query("SELECT ResultId FROM dbo.OperationalMetricPublicationResultArchive WITH (UPDLOCK,HOLDLOCK,INDEX(UQ_OMPRA_Slot)) WHERE OperationalMetricProjectionProcessorRowId=@P AND SourceRevisionPosition=@A;");
                var priorWitnesses = await Query("SELECT WitnessId FROM dbo.OperationalMetricConflictWitnessArchive WITH (UPDLOCK,HOLDLOCK) WHERE SourceRevisionPosition=@A AND MetricAggregationProcessorRowId=(SELECT MetricAggregationProcessorRowId FROM dbo.OperationalMetricProjectionProcessor WHERE OperationalMetricProjectionProcessorRowId=@P);");
                if (priorSlots.Count != 0 || priorWitnesses.Count != 0) throw Integrity("A proposed revision already contains partial archive-only publication.");
            }
            _ = await SqlContainmentArchiveTransaction.PrepareAsync(connection, transaction, frozen, replay, token,
                (stage, ct) => Inject(stage switch {
                    ContainmentArchiveStage.WitnessesInserted => ContainmentPublicationStage.WitnessesInserted,
                    ContainmentArchiveStage.ResultsInserted => ContainmentPublicationStage.ResultsInserted,
                    _ => ContainmentPublicationStage.ReferencesInserted }), historicalReads: historicalReads); // Retained comparisons precede archive DML.
            if (replay) { await transaction.CommitAsync(token); return; }
            foreach (var result in batch.Results)
                if (byHash.TryGetValue(Convert.ToHexString(result.KeyHash), out var old))
                {
                    if (!Equal(old["EvaluationKeyBinary"], result.Key)) throw Integrity("Evaluation-key digest collision.");
                    await Execute("DELETE dbo.OperationalMetricProjectionEvidence WHERE OperationalMetricProjectionRowId=@Id;", (long)old["OperationalMetricProjectionRowId"]!);
                }
            await Inject(ContainmentPublicationStage.EvidenceDeleted);
            await Execute("DELETE dbo.OperationalMetricProjectionManifest WHERE OperationalMetricProjectionProcessorRowId=@P;");
            await Inject(ContainmentPublicationStage.ManifestDeleted);
            var published = new List<(long Id, ContainmentArchiveBatch.Result Result)>();
            foreach (var result in batch.Results)
            {
                var row = Projection(processor, result); row.Remove("MachineOrderKey");
                await using var command = Command(""); Parameters(command, row);
                long id;
                if (byHash.TryGetValue(Convert.ToHexString(result.KeyHash), out var old))
                {
                    id = (long)old["OperationalMetricProjectionRowId"]!; command.Parameters.AddWithValue("@Id", id);
                    command.CommandText = "UPDATE dbo.OperationalMetricProjection SET " + string.Join(",", row.Keys.Select(k => k + "=@" + k)) + " WHERE OperationalMetricProjectionRowId=@Id;";
                    if (await command.ExecuteNonQueryAsync(token) != 1) throw Integrity("Retained update lost its locked row.");
                }
                else
                {
                    command.CommandText = "INSERT dbo.OperationalMetricProjection (" + string.Join(",", row.Keys) + ") OUTPUT INSERTED.OperationalMetricProjectionRowId VALUES (" + string.Join(",", row.Keys.Select(k => "@" + k)) + ");";
                    id = (long)(await command.ExecuteScalarAsync(token))!;
                }
                published.Add((id, result));
            }
            await Inject(ContainmentPublicationStage.ProjectionsWritten);
            foreach (var (id, result) in published)
            foreach (var row in Evidence(id, result.Document))
            {
                await using var command = Command("INSERT dbo.OperationalMetricProjectionEvidence (" + string.Join(",", row.Keys) + ") VALUES (" + string.Join(",", row.Keys.Select(k => "@" + k)) + ");");
                Parameters(command, row); await command.ExecuteNonQueryAsync(token);
            }
            await Inject(ContainmentPublicationStage.EvidenceWritten);
            foreach (var (id, _) in published) await Execute("INSERT dbo.OperationalMetricProjectionManifest (OperationalMetricProjectionProcessorRowId,OperationalMetricProjectionRowId) VALUES (@P,@Id);", id);
            await Inject(ContainmentPublicationStage.ManifestWritten);
            await Execute(checkpoint is null ? "INSERT dbo.OperationalMetricProjectionCheckpoint (OperationalMetricProjectionProcessorRowId,Position) VALUES (@P,@A);" : "UPDATE dbo.OperationalMetricProjectionCheckpoint SET Position=@A WHERE OperationalMetricProjectionProcessorRowId=@P;");
            await Inject(ContainmentPublicationStage.CheckpointWritten);
            // Re-read every retained row, including unchanged rows, before committing the new cut.
            var final = await Query("SELECT * FROM dbo.OperationalMetricProjection WHERE OperationalMetricProjectionProcessorRowId=@P ORDER BY EvaluationKeyHash;");
            if (final.Count != current.Count + published.Count(p => !byHash.ContainsKey(Convert.ToHexString(p.Result.KeyHash)))) throw Integrity("Retained set changed.");
            foreach (var row in final)
            {
                var id = (long)row["OperationalMetricProjectionRowId"]!; var incoming = published.SingleOrDefault(p => p.Id == id);
                if (incoming.Result is not null) Projection(processor, incoming.Result).Match(row);
                else foreach (var (name, value) in current.Single(r => (long)r["OperationalMetricProjectionRowId"]! == id))
                    if (!Equal(value, row[name])) throw Integrity("Unchanged retained row was mutated.");
                var finalEvidence = await Query("SELECT * FROM dbo.OperationalMetricProjectionEvidence WHERE OperationalMetricProjectionRowId=@Id ORDER BY EvidenceKind,EvidenceOrdinal;", id);
                if (incoming.Result is not null) MatchEvidence(Evidence(id, incoming.Result.Document), finalEvidence);
                else if (finalEvidence.Count != evidence[id].Count || finalEvidence.Where((e, i) => evidence[id][i].Any(f => !Equal(f.Value, e[f.Key]))).Any())
                    throw Integrity("Unchanged retained evidence was mutated.");
            }
            if (!(await Manifest()).SequenceEqual(published.Select(p => p.Id).Order()) || await ReadCheckpoint() != batch.Cut.Position.Value) throw Integrity("Final manifest/checkpoint differs.");
            await Inject(ContainmentPublicationStage.Revalidated);
            await transaction.CommitAsync(token);

            SqlCommand Command(string sql) { var c = connection.CreateCommand(); c.Transaction = transaction; c.CommandText = sql; return c; }
            async Task<List<Dictionary<string, object?>>> Query(string sql, long? id = null, byte[]? result = null, ulong? through = null)
            {
                await using var c = Command(sql); c.Parameters.AddWithValue("@P", processor);
                c.Parameters.Add(SqlServerUInt64.CreateParameter("@A", through ?? batch.Cut.Position.Value));
                if (id is not null) c.Parameters.AddWithValue("@Id", id.Value);
                if (result is not null) c.Parameters.Add("@Result", SqlDbType.Binary, 32).Value = result;
                return await Read(c, token);
            }
            async Task Execute(string sql, long? id = null)
            {
                await using var c = Command(sql); c.Parameters.AddWithValue("@P", processor);
                c.Parameters.Add(SqlServerUInt64.CreateParameter("@A", batch.Cut.Position.Value));
                if (id is not null) c.Parameters.AddWithValue("@Id", id.Value);
                await c.ExecuteNonQueryAsync(token);
            }
            async Task<ulong?> ReadCheckpoint()
            {
                var rows = await Query("SELECT Position FROM dbo.OperationalMetricProjectionCheckpoint WITH (UPDLOCK,HOLDLOCK) WHERE OperationalMetricProjectionProcessorRowId=@P;");
                return rows.Count == 0 ? null : SqlServerUInt64.Materialize((decimal)rows[0]["Position"]!);
            }
            async Task<long[]> Manifest() => (await Query("SELECT OperationalMetricProjectionRowId FROM dbo.OperationalMetricProjectionManifest WITH (UPDLOCK,HOLDLOCK) WHERE OperationalMetricProjectionProcessorRowId=@P ORDER BY OperationalMetricProjectionRowId;")).Select(r => (long)r["OperationalMetricProjectionRowId"]!).ToArray();
            async Task<long> Register()
            {
                await using var c = Command("SELECT OperationalMetricProjectionProcessorRowId FROM dbo.OperationalMetricProjectionProcessor WITH (UPDLOCK,HOLDLOCK) WHERE ProcessorKeyBinary=@Key;");
                c.Parameters.Add("@Key", SqlDbType.VarBinary, StringOrderKeyV2Codec.MaximumEncodedLength).Value = StringOrderKeyV2Codec.Encode(batch.Processor.Value);
                var existing = await c.ExecuteScalarAsync(token);
                if (existing is not null) return (long)existing; // Archive helper validates full format/policy/source before DML.
                c.CommandText = """
                    INSERT dbo.OperationalMetricProjectionProcessor
                    (ProcessorKeyBinary,ProcessorKey,MetricAggregationProcessorRowId,MetricInputStreamRowId,PublicationFormatVersion,EvaluationPolicyId)
                    OUTPUT INSERTED.OperationalMetricProjectionProcessorRowId
                    SELECT @Key,@Name,p.MetricAggregationProcessorRowId,p.MetricInputStreamRowId,2,@Policy
                    FROM dbo.MetricAggregationProcessor p JOIN dbo.MetricInputStream s ON s.MetricInputStreamRowId=p.MetricInputStreamRowId
                    WHERE p.ProcessorKeyBinary=@AP AND s.MachineId=@Machine AND s.StreamKeyBinary=@Stream;
                    """;
                c.Parameters.AddWithValue("@Name", batch.Processor.Value); c.Parameters.AddWithValue("@Policy", AvailabilityContainmentV1.PolicyId);
                c.Parameters.Add("@AP", SqlDbType.VarBinary, 512).Value = OrdinalStringKeyCodec.Encode(batch.Cut.ProcessorId.Value);
                c.Parameters.AddWithValue("@Machine", batch.Cut.StreamId.MachineId.Value);
                c.Parameters.Add("@Stream", SqlDbType.VarBinary, 512).Value = OrdinalStringKeyCodec.Encode(batch.Cut.StreamId.StreamKey);
                var inserted = await c.ExecuteScalarAsync(token);
                if (inserted is null) throw new MissingContainmentHistoricalAuthorityException("Publication source registration is absent.");
                await Inject(ContainmentPublicationStage.Registered); return (long)inserted;
            }
            async Task Inject(ContainmentPublicationStage stage)
            {
                token.ThrowIfCancellationRequested();
                if (failureInjection is not null) await failureInjection(stage, token);
                token.ThrowIfCancellationRequested();
            }
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); } catch { /* Keep primary failure. */ }
            throw;
        }
    }
    private static void MatchEvidence(Row[] expected, List<Dictionary<string, object?>> observed)
    {
        if (expected.Length != observed.Count) throw Integrity("Retained evidence inventory differs from canonical archive.");
        for (var i = 0; i < expected.Length; i++) expected[i].Match(observed[i]);
    }
    private static ContainmentHistoricalIntegrityException Integrity(string message) => new(message);
}
