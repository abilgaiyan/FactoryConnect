using System.Data;
using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;
using static FactoryConnect.Persistence.SqlServer.ContainmentRetainedState;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>Read-only format dispatch and complete retained publication verification in the reader's transaction.</summary>
internal static class SqlContainmentVerifiedRead
{
    internal sealed record Publication(IReadOnlyList<OperationalMetricProjection> Projections,
        IReadOnlySet<OperationalMetricEvaluationKey> LatestKeys);

    // Identity selects a stable isolation contract, never grants historical authority or selects another processor.
    internal static bool IsContainmentIdentity(OperationalMetricProjectionProcessorId processor)
    {
        var parts = processor.Value.Split(':');
        return parts.Length == 3 && Guid.TryParse(parts[1], out var machine) && machine != Guid.Empty &&
            processor == AvailabilityContainmentV1.ProjectionProcessorId(new MachineId(machine));
    }

    // null means a legacy/absent exact processor. Format 2 never falls back after failed verification.
    internal static async Task<Publication?> TryReadAsync(SqlConnection connection, SqlTransaction transaction,
        OperationalMetricProjectionProcessorId processor, CancellationToken token)
    {
        if (IsContainmentIdentity(processor))
        {
            SqlContainmentHistoricalContributionReader.RequireStableTransaction(connection, transaction);
            await SqlServerOperationalMetricProjectionProcessorGate.AcquireAsync(connection, transaction, processor,
                OperationalMetricProjectionProcessorGateMode.Shared, token);
        }
        using var verificationTiming = OperationalMetricReportingDiagnostics.Source.StartActivity("containment.publication.verify");
        verificationTiming?.SetTag("processor.id", processor.Value);
        await using (var schema = Command("SELECT COL_LENGTH('dbo.OperationalMetricProjectionProcessor','PublicationFormatVersion');"))
            if (await schema.ExecuteScalarAsync(token) is null or DBNull)
            {
                if (IsContainmentIdentity(processor)) throw Integrity("Containment schema is absent.");
                return null; // Existing V1 fixtures/contracts can precede migration 021.
            }
        var headers = await Query("""
            SELECT OperationalMetricProjectionProcessorRowId,ProcessorKey,ProcessorKeyBinary,PublicationFormatVersion,EvaluationPolicyId
            FROM dbo.OperationalMetricProjectionProcessor WHERE ProcessorKeyBinary=@Key;
            """, key: StringOrderKeyV2Codec.Encode(processor.Value));
        if (headers.Count == 0) return null;
        var header = headers.Single();
        if (!Equal(header["ProcessorKey"], processor.Value) || !Equal(header["ProcessorKeyBinary"], StringOrderKeyV2Codec.Encode(processor.Value)))
            throw Integrity("Exact processor identity is contradictory.");
        var format = (short)header["PublicationFormatVersion"]!;
        if (format == 1 && header["EvaluationPolicyId"] is null && !IsContainmentIdentity(processor)) return null;
        if (format != 2 || !Equal(header["EvaluationPolicyId"], AvailabilityContainmentV1.PolicyId) || !IsContainmentIdentity(processor))
            throw Integrity("Unsupported or contradictory processor publication format/policy.");
        SqlContainmentHistoricalContributionReader.RequireStableTransaction(connection, transaction);
        var id = (long)header["OperationalMetricProjectionProcessorRowId"]!;
        var source = await SqlServerOperationalMetricProjectionCommitTransaction.ReadCheckpointHeaderAsync(connection, transaction, processor, token);
        var retained = await Query("SELECT * FROM dbo.OperationalMetricProjection WHERE OperationalMetricProjectionProcessorRowId=@P ORDER BY EvaluationKeyHash;", id);
        var manifest = (await Query("SELECT OperationalMetricProjectionRowId FROM dbo.OperationalMetricProjectionManifest WHERE OperationalMetricProjectionProcessorRowId=@P ORDER BY OperationalMetricProjectionRowId;", id))
            .Select(r => (long)r["OperationalMetricProjectionRowId"]!).ToArray();
        var inventory = await Query("SELECT ResultId,SourceRevisionPosition FROM dbo.OperationalMetricPublicationResultArchive WHERE OperationalMetricProjectionProcessorRowId=@P ORDER BY ResultId;", id);
        if (source is null)
        {
            if (retained.Count != 0 || manifest.Length != 0 || inventory.Count != 0) throw Integrity("Publication state exists without its checkpoint.");
            return new Publication([], new HashSet<OperationalMetricEvaluationKey>());
        }
        if (processor != AvailabilityContainmentV1.ProjectionProcessorId(source.StreamId.MachineId)) throw Integrity("Containment identity does not bind the exact source machine.");
        if (retained.Count == 0) throw Integrity("Published checkpoint has no retained rows.");
        var latest = retained.Where(r => Equal(r["SourceRevisionPosition"], (decimal)source.Position.Value))
            .Select(r => (long)r["OperationalMetricProjectionRowId"]!).Order().ToArray();
        if (latest.Length == 0 || !latest.SequenceEqual(manifest)) throw Integrity("Latest manifest does not exactly cover its checkpoint.");
        var latestArchiveIds = inventory.Where(r => Equal(r["SourceRevisionPosition"], (decimal)source.Position.Value)).Select(r => Convert.ToHexString((byte[])r["ResultId"]!)).Order(StringComparer.Ordinal);
        var latestRetainedIds = retained.Where(r => manifest.Contains((long)r["OperationalMetricProjectionRowId"]!)).Select(r => r["ContainmentResultId"] is byte[] bytes ? Convert.ToHexString(bytes) : throw Integrity("Retained result binding is absent.")).Order(StringComparer.Ordinal);
        if (!latestArchiveIds.SequenceEqual(latestRetainedIds) || inventory.Any(r => (decimal)r["SourceRevisionPosition"]! > (decimal)source.Position.Value))
            throw Integrity("Archive inventory contradicts the reporting checkpoint.");
        var referenceTimeReads = new SqlContainmentReportingReferenceTimeScope(connection, transaction);
        var historicalReads = new SqlContainmentHistoricalReadScope(connection, transaction, referenceTimeReads);
        var projections = new List<OperationalMetricProjection>(); var latestKeys = new HashSet<OperationalMetricEvaluationKey>();
        foreach (var row in retained)
        {
            token.ThrowIfCancellationRequested();
            var rowId = (long)row["OperationalMetricProjectionRowId"]!;
            if (row["ContainmentResultId"] is not byte[] resultId) throw Integrity("Retained format-2 result binding is absent.");
            var archives = await Query("SELECT ResultPayload FROM dbo.OperationalMetricPublicationResultArchive WHERE ResultId=@Result;", result: resultId);
            if (archives.Count != 1) throw Integrity("Retained immutable archive is missing.");
            var bytes = (byte[])archives[0]["ResultPayload"]!;
            ContainmentArchiveBatch batch;
            try { batch = ContainmentArchiveBatch.Preflight([(ReadOnlyMemory<byte>)bytes]); }
            catch (InvalidDataException error) { throw new ContainmentHistoricalIntegrityException("Malformed retained result archive.", error); }
            var result = batch.Results[0];
            if (!result.Id.AsSpan().SequenceEqual(resultId) || batch.Processor != processor || batch.Cut.ProcessorId != source.AggregationProcessorId ||
                batch.Cut.StreamId != source.StreamId || batch.Cut.Position > source.Position)
                throw Integrity("Retained archive identity/source/cut contradicts exact processor binding.");
            Projection(id, result).Match(row);
            var expectedEvidence = Evidence(rowId, result.Document);
            var observedEvidence = await Query("SELECT * FROM dbo.OperationalMetricProjectionEvidence WHERE OperationalMetricProjectionRowId=@Row ORDER BY EvidenceKind,EvidenceOrdinal;", row: rowId);
            if (expectedEvidence.Length != observedEvidence.Count) throw Integrity("Retained evidence inventory differs from canonical evidence.");
            for (var i = 0; i < expectedEvidence.Length; i++) expectedEvidence[i].Match(observedEvidence[i]);
            // Read-only mode holds shared locks and has an explicit return before all archive DML.
            var handoffs = await SqlContainmentArchiveTransaction.VerifyExistingAsync(
                connection, transaction, [(ReadOnlyMemory<byte>)bytes], token, historicalReads);
            if (handoffs.Count != 1) throw Integrity("Retained result verification returned an unexpected handoff inventory.");
            var verified = handoffs[0].Read(connection, transaction, result.Id, result.Document.Key, batch.Cut, token);
            if (!OperationalMetricDependencySnapshotV2Codec.Encode(verified.Document).AsSpan().SequenceEqual(bytes))
                throw Integrity("Historical reconstruction differs from retained canonical result.");
            if (projections.Any(p => p.Key == verified.Projection.Key)) throw Integrity("Duplicate retained evaluation identity.");
            var core = verified.Projection;
            var metadata = new OperationalMetricPublicationMetadata(2, AvailabilityContainmentV1.PolicyId,
                Convert.ToHexString(result.Id), row["ConflictWitnessId"] is byte[] witnessId ? Convert.ToHexString(witnessId) : null);
            projections.Add(new OperationalMetricProjection(core.ProcessorId, core.Key, core.Status, core.Value, core.Unit,
                core.ReasonCode, core.ReasonOperandName, core.SourceRevision, core.OperandEvidence, core.DependencyEvidence,
                core.AvailabilityScopeConflictEvidence, core.EvaluationPolicyId, metadata));
            if (manifest.Contains(rowId)) latestKeys.Add(verified.Projection.Key);
        }
        verificationTiming?.SetTag("RootReconstructionInvocations", historicalReads.RootReconstructionInvocations);
        verificationTiming?.SetTag("ReferenceTimeAuthorityValidationExecutions", referenceTimeReads.ReferenceTimeAuthorityValidationExecutions);
        verificationTiming?.SetTag("ReferenceTimeAuthorityCacheHits", referenceTimeReads.CacheHits);
        verificationTiming?.SetTag("retained.result.count", retained.Count);
        verificationTiming?.SetTag("verification.completed", true);
        return new Publication(projections.AsReadOnly(), latestKeys);

        SqlCommand Command(string sql) { var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; return command; }
        async Task<List<Dictionary<string, object?>>> Query(string sql, long? processorId = null, byte[]? key = null, byte[]? result = null, long? row = null)
        {
            await using var command = Command(sql);
            if (processorId is not null) command.Parameters.AddWithValue("@P", processorId.Value);
            if (key is not null) command.Parameters.Add("@Key", SqlDbType.VarBinary, StringOrderKeyV2Codec.MaximumEncodedLength).Value = key;
            if (result is not null) command.Parameters.Add("@Result", SqlDbType.Binary, 32).Value = result;
            if (row is not null) command.Parameters.AddWithValue("@Row", row.Value);
            return await Read(command, token);
        }
    }
    private static ContainmentHistoricalIntegrityException Integrity(string message) => new(message);
}
