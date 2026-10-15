using System.Data;
using System.Security.Cryptography;
using FactoryConnect.Core.Oee;
using FactoryConnect.Core.Quality;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>Insert-only derived results. Quality must already be durably published; canonical projections are never written.</summary>
public sealed class SqlServerDerivedOeeAssessmentStore : IDerivedOeeAssessmentStore
{
    private const string Prefix = "derived-oee:v1:";
    private readonly string _connectionString;
    private readonly SqlServerQualityAssessmentStore _qualityStore;
    public SqlServerDerivedOeeAssessmentStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString; _qualityStore = new(connectionString);
    }
    public async ValueTask<QualityPublicationOutcome> PublishAsync(DerivedOeeAssessment assessment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assessment); cancellationToken.ThrowIfCancellationRequested();
        var payload = DerivedOeeStorageV1Codec.Encode(assessment); DerivedOeeStorageV1Codec.Decode(payload);
        var identity = DerivedOeeIdentity.Encode(assessment.Target, false); var key = SHA256.HashData(identity);
        await RequireQualityAsync(assessment, cancellationToken);
        await using var connection = new SqlConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using (var command = new SqlCommand("""
            DECLARE @Result int;
            EXEC @Result = sys.sp_getapplock @Resource = @Resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 15000;
            IF @Result < 0 THROW 51000, 'Derived OEE publication lock unavailable.', 1;
            """, connection, transaction))
        {
            command.Parameters.Add("@Resource", SqlDbType.NVarChar, 255).Value = "FactoryConnect.DerivedOee:" + Convert.ToHexString(key);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        var existing = await ReadAsync(connection, transaction, key, cancellationToken);
        if (existing is not null)
        {
            Validate(existing, assessment.Identity);
            if (!existing.Identity.AsSpan().SequenceEqual(identity) || !existing.Payload.AsSpan().SequenceEqual(payload))
                throw new InvalidDataException("Different derived OEE content under one identity.");
        }
        else
        {
            await using var command = new SqlCommand("""
                INSERT INTO dbo.DerivedOeeAssessment (IdentityHash, IdentityCodecVersion, IdentityBinary, PayloadCodecVersion, ContentHash, PayloadBinary)
                VALUES (@Key, 1, @Identity, 1, @Hash, @Payload);
                """, connection, transaction);
            Bytes(command, "@Key", key, 32); Bytes(command, "@Identity", identity);
            Bytes(command, "@Hash", SHA256.HashData(payload), 32); Bytes(command, "@Payload", payload);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return existing is null ? QualityPublicationOutcome.Appended : QualityPublicationOutcome.Replayed;
    }
    public async ValueTask<DerivedOeeReadResult> ReadByIdentityAsync(string identity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); var key = Key(identity);
        await using var connection = new SqlConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        var row = await ReadAsync(connection, null, key, cancellationToken);
        if (row is null) return new(QualityStorageReadOutcome.Absent, null);
        try
        {
            var assessment = Validate(row, identity); await RequireQualityAsync(assessment, cancellationToken);
            return new(QualityStorageReadOutcome.Found, assessment);
        }
        catch (NotSupportedException) { return new(QualityStorageReadOutcome.UnsupportedEncoding, null); }
        catch (InvalidDataException) { return new(QualityStorageReadOutcome.Corrupt, null); }
    }
    public async ValueTask<DerivedOeeReadResult> ReadExactAsync(DerivedOeeTarget? target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (target is null) return new(QualityStorageReadOutcome.IncomparableEvidence, null);
        var result = await ReadByIdentityAsync(DerivedOeeIdentity.Create(target), cancellationToken);
        if (result.Assessment is { } assessment) DerivedOeeSelector.Select(target, [assessment]);
        return result;
    }
    private async Task RequireQualityAsync(DerivedOeeAssessment assessment, CancellationToken token)
    {
        var read = await _qualityStore.ReadByIdentityAsync(assessment.Target.Quality.Identity, token);
        if (read.Outcome == QualityStorageReadOutcome.UnsupportedEncoding) throw new NotSupportedException("Unsupported durable Quality dependency.");
        if (read.Assessment is null || !QualityStorageV1Codec.EncodeAssessment(read.Assessment).AsSpan()
            .SequenceEqual(QualityStorageV1Codec.EncodeAssessment(assessment.Target.Quality)))
            throw new InvalidDataException("Exact durable Quality dependency is absent, corrupt or conflicting.");
    }
    private static DerivedOeeAssessment Validate(Row row, string identity)
    {
        if (row.IdentityVersion != 1 || row.PayloadVersion != 1) throw new NotSupportedException("Unsupported derived OEE encoding.");
        var key = Key(identity);
        if (row.Identity.Length == 0 || row.Identity.Length > DerivedOeeStorageV1Codec.MaximumLength ||
            row.Payload.Length == 0 || row.Payload.Length > DerivedOeeStorageV1Codec.MaximumLength ||
            !SHA256.HashData(row.Identity).AsSpan().SequenceEqual(key) || !SHA256.HashData(row.Payload).AsSpan().SequenceEqual(row.Hash))
            throw new InvalidDataException("Derived OEE envelope mismatch.");
        var assessment = DerivedOeeStorageV1Codec.Decode(row.Payload);
        if (assessment.Identity != identity || !row.Identity.AsSpan().SequenceEqual(DerivedOeeIdentity.Encode(assessment.Target, false)))
            throw new InvalidDataException("Decoded OEE identity mismatch.");
        return assessment;
    }
    private static byte[] Key(string identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        if (!identity.StartsWith(Prefix, StringComparison.Ordinal) || identity.Length != Prefix.Length + 64)
            throw new ArgumentException("Invalid OEE identity.", nameof(identity));
        var hex = identity[Prefix.Length..]; var key = Convert.FromHexString(hex);
        if (hex != Convert.ToHexString(key)) throw new ArgumentException("OEE identity is not canonical.", nameof(identity));
        return key;
    }
    private static async Task<Row?> ReadAsync(SqlConnection connection, SqlTransaction? transaction, byte[] key, CancellationToken token)
    {
        await using var command = new SqlCommand("""
            SELECT IdentityCodecVersion, IdentityBinary, PayloadCodecVersion, ContentHash, PayloadBinary
            FROM dbo.DerivedOeeAssessment WHERE IdentityHash = @Key;
            """, connection, transaction);
        Bytes(command, "@Key", key, 32); await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? new(reader.GetInt32(0), (byte[])reader.GetValue(1), reader.GetInt32(2), (byte[])reader.GetValue(3), (byte[])reader.GetValue(4)) : null;
    }
    private static void Bytes(SqlCommand command, string name, byte[] value, int size = -1) => command.Parameters.Add(name, SqlDbType.VarBinary, size).Value = value;
    private sealed record Row(int IdentityVersion, byte[] Identity, int PayloadVersion, byte[] Hash, byte[] Payload);
}
