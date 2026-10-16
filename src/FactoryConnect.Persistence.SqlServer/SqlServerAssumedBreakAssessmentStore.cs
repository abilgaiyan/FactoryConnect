using System.Data;
using System.Security.Cryptography;
using FactoryConnect.Core;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>Insert-only RR03 assessments; canonical projections are never written. Source authority is established by the producer.</summary>
public sealed class SqlServerAssumedBreakAssessmentStore : IAssumedBreakAssessmentStore
{
    private const string Prefix = "assumed-break-assessment:v1:";
    private readonly string _connectionString;
    public SqlServerAssumedBreakAssessmentStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }
    public async ValueTask<AssumedBreakPublicationOutcome> PublishAsync(DurableAssumedBreakAssessment assessment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assessment); cancellationToken.ThrowIfCancellationRequested();
        var payload = AssumedBreakAssessmentV1Codec.Encode(assessment); AssumedBreakAssessmentV1Codec.Decode(payload);
        var identity = AssumedBreakAssessmentV1Codec.EncodeTarget(assessment.Target); var key = SHA256.HashData(identity);
        await using var connection = new SqlConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using (var command = new SqlCommand("""
            DECLARE @Result int;
            EXEC @Result = sys.sp_getapplock @Resource = @Resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 15000;
            IF @Result < 0 THROW 51000, 'Assumed-break publication lock unavailable.', 1;
            """, connection, transaction))
        {
            command.Parameters.Add("@Resource", SqlDbType.NVarChar, 255).Value = "FactoryConnect.AssumedBreak:" + Convert.ToHexString(key);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        var existing = await ReadAsync(connection, transaction, key, cancellationToken);
        if (existing is not null)
        {
            Validate(existing, assessment.Identity);
            if (!existing.Identity.AsSpan().SequenceEqual(identity) || !existing.Payload.AsSpan().SequenceEqual(payload))
                throw new InvalidDataException("Different assumed-break content under one identity.");
        }
        else
        {
            await using var command = new SqlCommand("""
                INSERT INTO dbo.AssumedBreakAssessment (IdentityHash, IdentityCodecVersion, IdentityBinary, PayloadCodecVersion, ContentHash, PayloadBinary)
                VALUES (@Key, 1, @Identity, 1, @Hash, @Payload);
                """, connection, transaction);
            Bytes(command, "@Key", key, 32); Bytes(command, "@Identity", identity);
            Bytes(command, "@Hash", SHA256.HashData(payload), 32); Bytes(command, "@Payload", payload);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return existing is null ? AssumedBreakPublicationOutcome.Appended : AssumedBreakPublicationOutcome.Replayed;
    }
    public async ValueTask<AssumedBreakAssessmentReadResult> ReadByIdentityAsync(string identity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); var key = Key(identity);
        await using var connection = new SqlConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        var row = await ReadAsync(connection, null, key, cancellationToken);
        if (row is null) return new(AssumedBreakStorageReadOutcome.Absent, null);
        try
        {
            var assessment = Validate(row, identity);
            return new(AssumedBreakStorageReadOutcome.Found, assessment);
        }
        catch (NotSupportedException) { return new(AssumedBreakStorageReadOutcome.UnsupportedEncoding, null); }
        catch (InvalidDataException) { return new(AssumedBreakStorageReadOutcome.Corrupt, null); }
    }
    public async ValueTask<AssumedBreakAssessmentReadResult> ReadExactAsync(AssumedBreakAssessmentTarget? target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (target is null) return new(AssumedBreakStorageReadOutcome.IncomparableEvidence, null);
        string identity;
        try { identity = AssumedBreakAssessmentV1Codec.Identity(target); }
        catch (InvalidDataException) { return new(AssumedBreakStorageReadOutcome.IncomparableEvidence, null); }
        var result = await ReadByIdentityAsync(identity, cancellationToken);
        if (result.Assessment is { } assessment &&
            !AssumedBreakAssessmentV1Codec.EncodeTarget(target).AsSpan().SequenceEqual(
                AssumedBreakAssessmentV1Codec.EncodeTarget(assessment.Target)))
            return new(AssumedBreakStorageReadOutcome.Corrupt, null);
        return result;
    }
    private static DurableAssumedBreakAssessment Validate(Row row, string identity)
    {
        if (row.IdentityVersion != 1 || row.PayloadVersion != 1) throw new NotSupportedException("Unsupported assumed-break encoding.");
        var key = Key(identity);
        if (row.Identity.Length == 0 || row.Identity.Length > 1048576 ||
            row.Payload.Length == 0 || row.Payload.Length > 1048576 ||
            !SHA256.HashData(row.Identity).AsSpan().SequenceEqual(key) || !SHA256.HashData(row.Payload).AsSpan().SequenceEqual(row.Hash))
            throw new InvalidDataException("Assumed-break envelope mismatch.");
        var assessment = AssumedBreakAssessmentV1Codec.Decode(row.Payload, identity);
        if (assessment.Identity != identity || !row.Identity.AsSpan().SequenceEqual(AssumedBreakAssessmentV1Codec.EncodeTarget(assessment.Target)))
            throw new InvalidDataException("Decoded assumed-break identity mismatch.");
        return assessment;
    }
    private static byte[] Key(string identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        if (!identity.StartsWith(Prefix, StringComparison.Ordinal) || identity.Length != Prefix.Length + 64)
            throw new ArgumentException("Invalid assumed-break identity.", nameof(identity));
        var hex = identity[Prefix.Length..]; var key = Convert.FromHexString(hex);
        if (hex != Convert.ToHexString(key)) throw new ArgumentException("assumed-break identity is not canonical.", nameof(identity));
        return key;
    }
    private static async Task<Row?> ReadAsync(SqlConnection connection, SqlTransaction? transaction, byte[] key, CancellationToken token)
    {
        await using var command = new SqlCommand("""
            SELECT IdentityCodecVersion, IdentityBinary, PayloadCodecVersion, ContentHash, PayloadBinary
            FROM dbo.AssumedBreakAssessment WHERE IdentityHash = @Key;
            """, connection, transaction);
        Bytes(command, "@Key", key, 32); await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? new(reader.GetInt32(0), (byte[])reader.GetValue(1), reader.GetInt32(2), (byte[])reader.GetValue(3), (byte[])reader.GetValue(4)) : null;
    }
    private static void Bytes(SqlCommand command, string name, byte[] value, int size = -1) => command.Parameters.Add(name, SqlDbType.VarBinary, size).Value = value;
    private sealed record Row(int IdentityVersion, byte[] Identity, int PayloadVersion, byte[] Hash, byte[] Payload);
}
