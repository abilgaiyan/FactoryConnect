using System.Data;
using System.Security.Cryptography;
using FactoryConnect.Abstractions;
using FactoryConnect.Core.Quality;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>Explicitly composed storage only: no policy activation, source-authority discovery or runtime worker.</summary>
public sealed class SqlServerQualityAssessmentStore : IQualityAssessmentStore, IQualityPolicyStore
{
    private const string PolicyTable = "QualityPolicy";
    private const string AssessmentTable = "QualityAssessment";
    private const string AssessmentPrefix = "quality-assessment:v1:";
    private readonly string _connectionString;
    private readonly Action<string>? _stage;

    public SqlServerQualityAssessmentStore(string connectionString) : this(connectionString, null) { }
    internal SqlServerQualityAssessmentStore(string connectionString, Action<string>? publicationStage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString; _stage = publicationStage;
    }

    public async ValueTask<QualityPublicationOutcome> PublishPolicyAsync(ApprovedAllGoodQualityPolicy policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy); cancellationToken.ThrowIfCancellationRequested();
        var proposed = PolicyRow(policy);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await LockAsync(connection, transaction, PolicyTable, proposed.Key, cancellationToken);
        var stored = await ReadRowAsync(connection, transaction, PolicyTable, proposed.Key, cancellationToken);
        if (stored is not null) { ValidatePolicy(stored, proposed.Key); Compare(stored, proposed); }
        else { await InsertAsync(connection, transaction, PolicyTable, proposed, cancellationToken); _stage?.Invoke("PolicyInserted"); }
        await transaction.CommitAsync(cancellationToken); _stage?.Invoke("CommitAcknowledged");
        return stored is null ? QualityPublicationOutcome.Appended : QualityPublicationOutcome.Replayed;
    }

    public async ValueTask<QualityPublicationOutcome> PublishAsync(QualityAssessment assessment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assessment); cancellationToken.ThrowIfCancellationRequested();
        var proposed = AssessmentRow(assessment);
        // Validate bounds, canonical payload and calculated result before opening SQL.
        QualityStorageV1Codec.DecodeAssessment(proposed.Payload);
        var policies = assessment.Target.Policies.Select(PolicyRow).OrderBy(static row => Convert.ToHexString(row.Key), StringComparer.Ordinal).ToArray();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var missing = new List<Row>();
        // Universal publication order: policy hashes ascending, then assessment. Conflicts cause no writes.
        foreach (var policy in policies)
        {
            await LockAsync(connection, transaction, PolicyTable, policy.Key, cancellationToken);
            var storedPolicy = await ReadRowAsync(connection, transaction, PolicyTable, policy.Key, cancellationToken);
            if (storedPolicy is null) missing.Add(policy);
            else { ValidatePolicy(storedPolicy, policy.Key); Compare(storedPolicy, policy); }
        }
        await LockAsync(connection, transaction, AssessmentTable, proposed.Key, cancellationToken);
        var stored = await ReadRowAsync(connection, transaction, AssessmentTable, proposed.Key, cancellationToken);
        if (stored is not null)
        {
            ValidateAssessment(stored, assessment.Identity);
            Compare(stored, proposed);
            if (missing.Count != 0) throw new InvalidDataException("Assessment references missing durable policies.");
        }
        else
        {
            foreach (var policy in missing)
            {
                await InsertAsync(connection, transaction, PolicyTable, policy, cancellationToken);
                _stage?.Invoke("PolicyInserted");
            }
            await InsertAsync(connection, transaction, AssessmentTable, proposed, cancellationToken);
            _stage?.Invoke("AssessmentInserted");
        }
        await transaction.CommitAsync(cancellationToken); _stage?.Invoke("CommitAcknowledged");
        return stored is null ? QualityPublicationOutcome.Appended : QualityPublicationOutcome.Replayed;
    }

    public async ValueTask<QualityPolicyReadResult> ReadPolicyAsync(QualityPolicyVersion identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity); cancellationToken.ThrowIfCancellationRequested();
        var key = SHA256.HashData(QualityStorageV1Codec.PolicyIdentity(identity));
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var stored = await ReadRowAsync(connection, null, PolicyTable, key, cancellationToken);
        if (stored is null) return new(QualityStorageReadOutcome.Absent, null);
        try
        {
            var policy = ValidatePolicy(stored, key);
            if (policy.Identity != identity) throw new InvalidDataException("Policy identity collision.");
            return new(QualityStorageReadOutcome.Found, policy);
        }
        catch (NotSupportedException) { return new(QualityStorageReadOutcome.UnsupportedEncoding, null); }
        catch (InvalidDataException) { return new(QualityStorageReadOutcome.Corrupt, null); }
    }

    public async ValueTask<QualityAssessmentReadResult> ReadByIdentityAsync(string identity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = AssessmentKey(identity);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        // Publication is insert-only and commits policies + assessment atomically. Read committed cannot
        // expose an assessment before its policies; no mutable head or latest-timestamp lookup exists.
        var stored = await ReadRowAsync(connection, null, AssessmentTable, key, cancellationToken);
        if (stored is null) return new(QualityStorageReadOutcome.Absent, null);
        try
        {
            var assessment = ValidateAssessment(stored, identity);
            foreach (var policy in assessment.Target.Policies)
            {
                var expected = PolicyRow(policy);
                var durable = await ReadRowAsync(connection, null, PolicyTable, expected.Key, cancellationToken)
                    ?? throw new InvalidDataException("Referenced policy is absent.");
                ValidatePolicy(durable, expected.Key); Compare(durable, expected);
            }
            return new(QualityStorageReadOutcome.Found, assessment);
        }
        catch (NotSupportedException) { return new(QualityStorageReadOutcome.UnsupportedEncoding, null); }
        catch (InvalidDataException) { return new(QualityStorageReadOutcome.Corrupt, null); }
    }

    public async ValueTask<QualityAssessmentReadResult> ReadExactAsync(QualityAssessmentTarget? target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (target is null) return new(QualityStorageReadOutcome.IncomparableEvidence, null);
        var result = await ReadByIdentityAsync(QualityAssessmentIdentity.Create(target), cancellationToken);
        if (result.Assessment is { } assessment)
        {
            // A changed payload under immutable references is an integrity failure, never absence.
            QualityAssessmentSelector.Select(target, [assessment]);
        }
        return result;
    }

    private static Row PolicyRow(ApprovedAllGoodQualityPolicy policy)
    {
        var identity = QualityStorageV1Codec.PolicyIdentity(policy.Identity);
        var payload = QualityStorageV1Codec.EncodePolicy(policy);
        QualityStorageV1Codec.DecodePolicy(payload);
        return new(SHA256.HashData(identity), 1, identity, 1, SHA256.HashData(payload), payload);
    }
    private static Row AssessmentRow(QualityAssessment assessment)
    {
        var identity = QualityAssessmentIdentity.Encode(assessment.Target, false);
        var payload = QualityStorageV1Codec.EncodeAssessment(assessment);
        return new(SHA256.HashData(identity), 1, identity, 1, SHA256.HashData(payload), payload);
    }
    private static void ValidateEnvelope(Row row, byte[] key)
    {
        if (row.IdentityVersion != 1 || row.PayloadVersion != QualityStorageV1Codec.Version)
            throw new NotSupportedException("Unsupported Quality storage encoding.");
        if (row.Identity.Length == 0 || row.Identity.Length > QualityStorageV1Codec.MaximumPayloadLength ||
            row.Payload.Length == 0 || row.Payload.Length > QualityStorageV1Codec.MaximumPayloadLength ||
            !row.Key.AsSpan().SequenceEqual(key) || !SHA256.HashData(row.Identity).AsSpan().SequenceEqual(key) ||
            !SHA256.HashData(row.Payload).AsSpan().SequenceEqual(row.ContentHash))
            throw new InvalidDataException("Quality storage envelope integrity failure.");
    }
    private static ApprovedAllGoodQualityPolicy ValidatePolicy(Row row, byte[] key)
    {
        ValidateEnvelope(row, key);
        var policy = QualityStorageV1Codec.DecodePolicy(row.Payload);
        if (!row.Identity.AsSpan().SequenceEqual(QualityStorageV1Codec.PolicyIdentity(policy.Identity)))
            throw new InvalidDataException("Decoded policy identity mismatch.");
        return policy;
    }
    private static QualityAssessment ValidateAssessment(Row row, string identity)
    {
        ValidateEnvelope(row, AssessmentKey(identity));
        var assessment = QualityStorageV1Codec.DecodeAssessment(row.Payload);
        if (!string.Equals(identity, assessment.Identity, StringComparison.Ordinal) ||
            !row.Identity.AsSpan().SequenceEqual(QualityAssessmentIdentity.Encode(assessment.Target, false)))
            throw new InvalidDataException("Decoded assessment identity mismatch.");
        return assessment;
    }
    private static void Compare(Row stored, Row proposed)
    {
        if (!stored.Identity.AsSpan().SequenceEqual(proposed.Identity) || !stored.Payload.AsSpan().SequenceEqual(proposed.Payload))
            throw new InvalidDataException("Different Quality content under an immutable identity.");
    }
    private static byte[] AssessmentKey(string identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        if (!identity.StartsWith(AssessmentPrefix, StringComparison.Ordinal) || identity.Length != AssessmentPrefix.Length + 64)
            throw new ArgumentException("Invalid assessment identity.", nameof(identity));
        var hex = identity[AssessmentPrefix.Length..];
        byte[] key;
        try { key = Convert.FromHexString(hex); }
        catch (FormatException exception) { throw new ArgumentException("Invalid assessment identity.", nameof(identity), exception); }
        if (!string.Equals(hex, Convert.ToHexString(key), StringComparison.Ordinal))
            throw new ArgumentException("Assessment identity must use canonical uppercase hex.", nameof(identity));
        return key;
    }
    private static async Task LockAsync(SqlConnection connection, SqlTransaction transaction, string table, byte[] key, CancellationToken token)
    {
        await using var command = Command(connection, transaction, """
            DECLARE @Result int;
            EXEC @Result = sys.sp_getapplock @Resource = @Resource, @LockMode = 'Exclusive',
                @LockOwner = 'Transaction', @LockTimeout = 15000;
            IF @Result < 0 THROW 51000, 'Quality publication lock unavailable.', 1;
            """);
        command.Parameters.Add("@Resource", SqlDbType.NVarChar, 255).Value = "FactoryConnect.Quality:" + table + ":" + Convert.ToHexString(key);
        await command.ExecuteNonQueryAsync(token);
    }
    private static async Task<Row?> ReadRowAsync(SqlConnection connection, SqlTransaction? transaction, string table, byte[] key, CancellationToken token)
    {
        // table is always one of the two private constants, never external input.
        await using var command = Command(connection, transaction,
            $"SELECT IdentityHash, IdentityCodecVersion, IdentityBinary, PayloadCodecVersion, ContentHash, PayloadBinary FROM dbo.{table} WHERE IdentityHash = @Key;");
        Bytes(command, "@Key", key, 32);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token)
            ? new Row((byte[])reader.GetValue(0), reader.GetInt32(1), (byte[])reader.GetValue(2), reader.GetInt32(3), (byte[])reader.GetValue(4), (byte[])reader.GetValue(5)) : null;
    }
    private static async Task InsertAsync(SqlConnection connection, SqlTransaction transaction, string table, Row row, CancellationToken token)
    {
        await using var command = Command(connection, transaction,
            $"INSERT INTO dbo.{table} (IdentityHash, IdentityCodecVersion, IdentityBinary, PayloadCodecVersion, ContentHash, PayloadBinary) VALUES (@Key, @IdentityVersion, @Identity, @PayloadVersion, @ContentHash, @Payload);");
        Bytes(command, "@Key", row.Key, 32); Bytes(command, "@Identity", row.Identity);
        Bytes(command, "@ContentHash", row.ContentHash, 32); Bytes(command, "@Payload", row.Payload);
        command.Parameters.Add("@IdentityVersion", SqlDbType.Int).Value = row.IdentityVersion;
        command.Parameters.Add("@PayloadVersion", SqlDbType.Int).Value = row.PayloadVersion;
        await command.ExecuteNonQueryAsync(token);
    }
    private static SqlCommand Command(SqlConnection connection, SqlTransaction? transaction, string text) => new(text, connection, transaction);
    private static void Bytes(SqlCommand command, string name, byte[] value, int size = -1) => command.Parameters.Add(name, SqlDbType.VarBinary, size).Value = value;
    private sealed record Row(byte[] Key, int IdentityVersion, byte[] Identity, int PayloadVersion, byte[] ContentHash, byte[] Payload);
}
