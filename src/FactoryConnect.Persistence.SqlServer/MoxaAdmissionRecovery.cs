using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Core.Quality;
using FactoryConnect.Core.Oee;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>Single-purpose MOXA correction. Caller must retain exclusive deployment ownership
/// and confirm the Edge writer is absent. This API does not stop processes or activate configuration.</summary>
public static class MoxaAdmissionRecovery
{
    public static readonly Guid Machine = Guid.Parse("bfa0dc97-9a68-45ed-b189-1dacb7d863f0");
    public const string Processor = "production-context:bfa0dc97-9a68-45ed-b189-1dacb7d863f0";
    public const string Stream = "mtconnect:DEWAS-SR-MOXA-001";
    public static readonly DateTimeOffset Boundary = new(2026, 10, 25, 2, 0, 0, TimeSpan.Zero);

    public static Task ExecuteAsync(string connectionString, Guid operationId, string expectedServer,
        string expectedDatabase, string approvalReference, string maintenanceEvidence,
        CancellationToken cancellationToken) => ExecuteCoreAsync(connectionString, operationId,
            expectedServer, expectedDatabase, approvalReference, maintenanceEvidence, null, cancellationToken);

    // Fault/coordination seam is internal and has no CLI or production configuration switch.
    internal static async Task ExecuteCoreAsync(string connectionString, Guid operationId,
        string expectedServer, string expectedDatabase, string approvalReference,
        string maintenanceEvidence, Func<Task>? afterAudit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedServer);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedDatabase);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(maintenanceEvidence);
        if (operationId == Guid.Empty) throw new ArgumentException("Operation identity is required.", nameof(operationId));
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var migrationScope = await SqlServerMigrationTransactionScope.BeginSharedAsync(connection, TimeSpan.FromSeconds(5), cancellationToken);
        var transaction = migrationScope.Transaction;
        var schema = await new SqlServerSchemaMetadataReader().ReadFactoryConnectOwnedSchemaInTransactionAsync(connection, transaction, cancellationToken);
        if (!SqlSchemaComparator.Compare(SqlRepositorySchemaDescriptors.Current, schema).IsExactMatch)
            throw new InvalidOperationException("Recovery requires the exact current schema with trusted ownership constraints.");
        // Disposal rolls back on all failures, including cancellation and the internal test seam.
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 30;
        command.CommandText = "SET XACT_ABORT ON; SET LOCK_TIMEOUT 5000; SELECT CONVERT(nvarchar(256), SERVERPROPERTY('ServerName')), DB_NAME();";
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken) ||
                !string.Equals(reader.GetString(0), expectedServer, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(reader.GetString(1), expectedDatabase, StringComparison.Ordinal))
                throw new InvalidOperationException("Recovery database/server identity mismatch.");
        }
        var processorBytes = OrdinalStringKeyCodec.Encode(Processor);
        var streamBytes = OrdinalStringKeyCodec.Encode(Stream);
        command.CommandText = """
            SELECT ProcessorKey, ProcessorKeyBinary, ProcessorKeyHash, MachineId,
                   ObservationStreamKey, ObservationStreamKeyBinary, ObservationStreamKeyHash,
                   AdmissionBound, ProductionStartsAt
            FROM dbo.ProductionContextProcessor WITH (UPDLOCK, HOLDLOCK)
            WHERE ProductionContextProcessorRowId = 2;
            """;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) != Processor ||
                !((byte[])reader[1]).AsSpan().SequenceEqual(processorBytes) ||
                !((byte[])reader[2]).AsSpan().SequenceEqual(SHA256.HashData(processorBytes)) ||
                reader.GetGuid(3) != Machine || reader.GetString(4) != Stream ||
                !((byte[])reader[5]).AsSpan().SequenceEqual(streamBytes) ||
                !((byte[])reader[6]).AsSpan().SequenceEqual(SHA256.HashData(streamBytes)) ||
                !reader.GetBoolean(7) || !reader.IsDBNull(8))
                throw new InvalidOperationException("Exact initial MOXA binding is required; recovery aborted.");
        }
        command.CommandText = "SELECT COUNT_BIG(*) FROM dbo.ProductionContextCheckpoint WITH (UPDLOCK, HOLDLOCK) WHERE ProductionContextProcessorRowId = 2;";
        if ((long)(await command.ExecuteScalarAsync(cancellationToken))! != 0)
            throw new InvalidOperationException("A checkpoint exists; recovery prohibited.");

        // No text matching of opaque identities: validate every stored output's integrity,
        // typed machine ownership, and contextual source ownership. Ambiguity fails closed.
        var contexts = new Dictionary<string, Guid>(StringComparer.Ordinal);
        command.CommandText = "SELECT IdentityText, IdentityBinary, IdentityHash, PayloadHash, Payload FROM dbo.ContextualizedActivityOutput WITH (HOLDLOCK);";
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var payload = ValidateEnvelope(reader);
                var item = JsonSerializer.Deserialize<ContextualizedActivityInterval>(payload)
                    ?? throw new InvalidOperationException("Missing activity payload.");
                if (item.Id.Value != reader.GetString(0) || item.MachineId.IsEmpty ||
                    item.SourceStreamId is null || item.SourceStreamId.MachineId != item.MachineId ||
                    item.SourceProcessorId is null || string.IsNullOrWhiteSpace(item.SourceProcessorId.Value))
                    throw new InvalidOperationException("Unresolved contextual output ownership.");
                if (item.MachineId.Value == Machine || item.SourceProcessorId.Value == Processor ||
                    item.SourceStreamId.StreamKey == Stream)
                    throw new InvalidOperationException("MOXA contextual output exists.");
                contexts.Add(item.Id.Value, item.MachineId.Value);
            }
        }
        command.CommandText = "SELECT IdentityText, IdentityBinary, IdentityHash, PayloadHash, Payload FROM dbo.ProductionTimeEligibilityOutput WITH (HOLDLOCK);";
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var payload = ValidateEnvelope(reader);
                var item = JsonSerializer.Deserialize<ProductionTimeEligibilityInterval>(payload)
                    ?? throw new InvalidOperationException("Missing eligibility payload.");
                if (item.Id.Value != reader.GetString(0) || item.MachineId.IsEmpty ||
                    !contexts.TryGetValue(item.SourceContextualizedActivityIntervalId.Value, out var owner) ||
                    owner != item.MachineId.Value)
                    throw new InvalidOperationException("Unresolved eligibility source ownership.");
                if (owner == Machine) throw new InvalidOperationException("MOXA eligibility output exists.");
            }
        }
        await VerifyAssessmentsAsync(command, cancellationToken);
        command.Parameters.Add("@Machine", SqlDbType.UniqueIdentifier).Value = Machine;
        // Serializable predicates retain range locks, including empty streams. Existing FK
        // contracts bind contributions/revisions/report evidence to these parents.
        foreach (var table in new[] { "MetricInputStream", "MetricInputFact", "ShiftMetricAggregate", "ProductionDayMetricAggregate", "OperationalMetricProjection" })
        {
            command.CommandText = $"SELECT COUNT_BIG(*) FROM dbo.{table} WITH (HOLDLOCK) WHERE MachineId = @Machine;";
            if ((long)(await command.ExecuteScalarAsync(cancellationToken))! != 0)
                throw new InvalidOperationException($"MOXA evidence exists in {table}.");
        }
        command.CommandText = "SELECT COUNT_BIG(*) FROM dbo.ProductionAdmissionRecoveryAudit WITH (UPDLOCK, HOLDLOCK) WHERE ProductionContextProcessorRowId = 2 OR OperationId = @Operation;";
        command.Parameters.Add("@Operation", SqlDbType.UniqueIdentifier).Value = operationId;
        if ((long)(await command.ExecuteScalarAsync(cancellationToken))! != 0)
            throw new InvalidOperationException("Recovery already recorded; inspect audit, do not replay.");
        var evidence = JsonSerializer.Serialize(new { approvalReference, maintenanceEvidence,
            expectedServer, expectedDatabase, Processor, Stream, Machine, sourceCommit = "3a8d130f6e7ac4536ccc6cebce46a77e6dcf291f",
            zeroOutputProofVersion = 1, inspectedContextOutputs = contexts.Count });
        command.Parameters.Add("@Evidence", SqlDbType.NVarChar, -1).Value = evidence;
        command.Parameters.Add("@Boundary", SqlDbType.DateTimeOffset).Value = Boundary;
        command.CommandText = """
            INSERT dbo.ProductionAdmissionRecoveryAudit
              (OperationId, ProductionContextProcessorRowId, SchemaVersion, BeforeBoundary, AfterBoundary, RecordedAtUtc, Evidence)
            VALUES (@Operation, 2, 1, NULL, @Boundary, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'), @Evidence);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        if (afterAudit is not null) await afterAudit();
        command.CommandText = """
            UPDATE dbo.ProductionContextProcessor SET ProductionStartsAt = @Boundary
            WHERE ProductionContextProcessorRowId = 2 AND MachineId = @Machine
              AND AdmissionBound = 1 AND ProductionStartsAt IS NULL;
            """;
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Recovery update did not affect exactly one processor.");
        await migrationScope.CommitAsync(cancellationToken);
    }

    private static async Task VerifyAssessmentsAsync(SqlCommand command, CancellationToken token)
    {
        foreach (var table in new[] { "QualityAssessment", "DerivedOeeAssessment", "AssumedBreakAssessment" })
        {
            command.CommandText = $"SELECT IdentityHash, IdentityCodecVersion, IdentityBinary, PayloadCodecVersion, ContentHash, PayloadBinary FROM dbo.{table} WITH (HOLDLOCK);";
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var identityHash = (byte[])reader[0];
                var identity = (byte[])reader[2];
                var payload = (byte[])reader[5];
                if (reader.GetInt32(1) != 1 || reader.GetInt32(3) != 1 ||
                    !SHA256.HashData(identity).AsSpan().SequenceEqual(identityHash) ||
                    !SHA256.HashData(payload).AsSpan().SequenceEqual((byte[])reader[4]))
                    throw new InvalidOperationException("Unresolved assessment envelope.");
                Guid machine;
                byte[] expectedIdentity;
                if (table == "QualityAssessment")
                {
                    var item = QualityStorageV1Codec.DecodeAssessment(payload);
                    machine = item.Target.Scope.MachineId.Value;
                    expectedIdentity = QualityAssessmentIdentity.Encode(item.Target, false);
                }
                else if (table == "DerivedOeeAssessment")
                {
                    var item = DerivedOeeStorageV1Codec.Decode(payload);
                    machine = item.Target.Quality.Target.Scope.MachineId.Value;
                    expectedIdentity = DerivedOeeIdentity.Encode(item.Target, false);
                }
                else
                {
                    var item = AssumedBreakAssessmentV1Codec.Decode(payload, "assumed-break-assessment:v1:" + Convert.ToHexString(identityHash));
                    machine = item.Target.MachineId.Value;
                    expectedIdentity = AssumedBreakAssessmentV1Codec.EncodeTarget(item.Target);
                }
                if (machine == Guid.Empty || !expectedIdentity.AsSpan().SequenceEqual(identity))
                    throw new InvalidOperationException("Unresolved assessment machine identity.");
                if (machine == Machine) throw new InvalidOperationException("MOXA assessment exists.");
            }
        }
    }

    private static string ValidateEnvelope(SqlDataReader reader)
    {
        var identity = OrdinalStringKeyCodec.Encode(reader.GetString(0));
        var payload = reader.GetString(4);
        if (!((byte[])reader[1]).AsSpan().SequenceEqual(identity) ||
            !((byte[])reader[2]).AsSpan().SequenceEqual(SHA256.HashData(identity)) ||
            !((byte[])reader[3]).AsSpan().SequenceEqual(SHA256.HashData(Encoding.UTF8.GetBytes(payload))))
            throw new InvalidOperationException("Output identity/payload integrity is unresolved.");
        // Reject duplicate JSON properties: deserializer last-value wins is not ownership proof.
        using var document = JsonDocument.Parse(payload);
        RejectDuplicates(document.RootElement);
        return payload;
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidOperationException("Duplicate output property.");
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }
}
