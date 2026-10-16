using System.Data;
using System.Security.Cryptography;
using FactoryConnect.Abstractions;
using FactoryConnect.Core.AssumedBreakAuthority;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>Immutable configuration evidence and separate explicit admission. No Running authority is inferred.</summary>
public sealed class SqlServerAssumedBreakAuthorityStore : IAssumedBreakAuthorityStore
{
    private readonly string _connectionString;
    public SqlServerAssumedBreakAuthorityStore(string connectionString)
    { ArgumentException.ThrowIfNullOrWhiteSpace(connectionString); _connectionString = connectionString; }

    public ValueTask<BreakAuthorityPublicationOutcome> PublishSnapshotAsync(AssumedBreakConfigurationSnapshot snapshot,
        string expectedIdentity, CancellationToken cancellationToken) => PublishAsync(snapshot, expectedIdentity, cancellationToken);
    public ValueTask<BreakAuthorityPublicationOutcome> PublishAdmissionAsync(AssumedBreakConfigurationAdmission admission,
        string expectedIdentity, CancellationToken cancellationToken) => PublishAsync(admission, expectedIdentity, cancellationToken);

    private async ValueTask<BreakAuthorityPublicationOutcome> PublishAsync(AssumedBreakAuthorityRecord value,
        string expectedIdentity, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(value); token.ThrowIfCancellationRequested();
        var payload = AssumedBreakAuthorityV1Codec.Encode(value);
        if (value.Identity != expectedIdentity) throw new InvalidDataException("Publication identity/content conflict.");
        var snapshot = value is AssumedBreakConfigurationSnapshot;
        var key = AssumedBreakAuthorityV1Codec.Key(expectedIdentity, snapshot);
        await using var connection = new SqlConnection(_connectionString); await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        await using (var command = new SqlCommand("""
            DECLARE @Result int;
            EXEC @Result = sys.sp_getapplock @Resource = @Resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 15000;
            IF @Result < 0 THROW 51000, 'Assumed-break authority publication lock unavailable.', 1;
            """, connection, transaction))
        {
            command.Parameters.Add("@Resource", SqlDbType.NVarChar, 255).Value = "FactoryConnect.BreakAuthority:" + expectedIdentity;
            await command.ExecuteNonQueryAsync(token);
        }
        byte[]? dependency = null;
        if (value is AssumedBreakConfigurationAdmission admission)
        {
            dependency = AssumedBreakAuthorityV1Codec.Key(admission.SnapshotIdentity, true);
            var source = await ReadCoreAsync(connection, transaction, admission.SnapshotIdentity, true, token);
            if (source is not AssumedBreakConfigurationSnapshot configuration)
                throw new InvalidDataException("Exact configuration snapshot is absent.");
            admission.ValidateAgainst(configuration);
        }
        var existing = await ReadCoreAsync(connection, transaction, expectedIdentity, snapshot, token);
        if (existing is not null)
        {
            if (!AssumedBreakAuthorityV1Codec.Encode(existing).AsSpan().SequenceEqual(payload))
                throw new InvalidDataException("Different content under immutable authority identity.");
        }
        else
        {
            var table = Table(snapshot);
            await using var command = new SqlCommand(
                $"INSERT INTO dbo.{table} (IdentityHash, IdentityCodecVersion, IdentityBinary, PayloadCodecVersion, ContentHash, PayloadBinary" +
                (snapshot ? ") VALUES (@Key, 1, @Payload, 1, @Hash, @Payload);" : ", SnapshotIdentityHash) VALUES (@Key, 1, @Payload, 1, @Hash, @Payload, @Snapshot);"), connection, transaction);
            Bytes(command, "@Key", key, 32); Bytes(command, "@Payload", payload); Bytes(command, "@Hash", SHA256.HashData(payload), 32);
            if (dependency is not null) Bytes(command, "@Snapshot", dependency, 32);
            await command.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
        return existing is null ? BreakAuthorityPublicationOutcome.Appended : BreakAuthorityPublicationOutcome.Replayed;
    }

    public async ValueTask<BreakAuthorityReadResult<AssumedBreakConfigurationSnapshot>> ReadSnapshotAsync(string identity, CancellationToken cancellationToken)
    {
        var result = await ReadAsync(identity, true, cancellationToken);
        return new(result.Outcome, result.Value as AssumedBreakConfigurationSnapshot);
    }
    public async ValueTask<BreakAuthorityReadResult<AssumedBreakConfigurationAdmission>> ReadAdmissionAsync(string identity, CancellationToken cancellationToken)
    {
        var result = await ReadAsync(identity, false, cancellationToken);
        return new(result.Outcome, result.Value as AssumedBreakConfigurationAdmission);
    }
    private async ValueTask<BreakAuthorityReadResult<AssumedBreakAuthorityRecord>> ReadAsync(string identity, bool snapshot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            _ = AssumedBreakAuthorityV1Codec.Key(identity, snapshot);
            await using var connection = new SqlConnection(_connectionString); await connection.OpenAsync(token);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var value = await ReadCoreAsync(connection, transaction, identity, snapshot, token);
            if (value is AssumedBreakConfigurationAdmission admission)
            {
                var source = await ReadCoreAsync(connection, transaction, admission.SnapshotIdentity, true, token);
                if (source is not AssumedBreakConfigurationSnapshot configuration) throw new InvalidDataException("Missing admitted snapshot.");
                admission.ValidateAgainst(configuration);
            }
            await transaction.CommitAsync(token);
            return new(value is null ? BreakAuthorityReadOutcome.Absent : BreakAuthorityReadOutcome.Found, value);
        }
        catch (NotSupportedException) { return new(BreakAuthorityReadOutcome.UnsupportedEncoding, null); }
        catch (InvalidDataException) { return new(BreakAuthorityReadOutcome.Corrupt, null); }
    }

    public async ValueTask<BreakAuthorityReadResult<AssumedBreakConfigurationAdmission>> SelectAdmissionAsync(
        string snapshotIdentity, SiteId siteId, MachineId machineId, DateOnly productionDate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var key = AssumedBreakAuthorityV1Codec.Key(snapshotIdentity, true);
            await using var connection = new SqlConnection(_connectionString); await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var source = await ReadCoreAsync(connection, transaction, snapshotIdentity, true, cancellationToken);
            if (source is not AssumedBreakConfigurationSnapshot configuration) return new(BreakAuthorityReadOutcome.Absent, null);
            if (configuration.SiteId != siteId || configuration.MachineId != machineId || configuration.ProductionDate != productionDate)
                return new(BreakAuthorityReadOutcome.IncomparableEvidence, null);
            var identities = new List<string>();
            await using (var command = new SqlCommand("SELECT IdentityHash FROM dbo.AssumedBreakConfigurationAdmission WHERE SnapshotIdentityHash = @Snapshot;", connection, transaction))
            {
                Bytes(command, "@Snapshot", key, 32);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    identities.Add("assumed-break-admission:v1:" + Convert.ToHexString((byte[])reader.GetValue(0)));
            }
            var matches = new List<AssumedBreakConfigurationAdmission>();
            foreach (var identity in identities)
            {
                var candidate = await ReadCoreAsync(connection, transaction, identity, false, cancellationToken);
                if (candidate is not AssumedBreakConfigurationAdmission admission)
                    throw new InvalidDataException("Admission disappeared from a serializable candidate set.");
                admission.ValidateAgainst(configuration);
                if (admission.SiteId == siteId && admission.MachineId == machineId && productionDate >= admission.EffectiveFrom &&
                    (admission.EffectiveTo is null || productionDate < admission.EffectiveTo.Value)) matches.Add(admission);
            }
            await transaction.CommitAsync(cancellationToken);
            return matches.Count switch
            {
                0 => new(BreakAuthorityReadOutcome.Absent, null),
                1 => new(BreakAuthorityReadOutcome.Found, matches[0]),
                _ => new(BreakAuthorityReadOutcome.Ambiguous, null),
            };
        }
        catch (NotSupportedException) { return new(BreakAuthorityReadOutcome.UnsupportedEncoding, null); }
        catch (InvalidDataException) { return new(BreakAuthorityReadOutcome.Corrupt, null); }
    }

    private static async Task<AssumedBreakAuthorityRecord?> ReadCoreAsync(SqlConnection connection, SqlTransaction? transaction,
        string identity, bool snapshot, CancellationToken token)
    {
        var key = AssumedBreakAuthorityV1Codec.Key(identity, snapshot);
        var hint = transaction is null ? string.Empty : " WITH (HOLDLOCK)";
        await using var command = new SqlCommand(
            "SELECT IdentityCodecVersion, IdentityBinary, PayloadCodecVersion, ContentHash, PayloadBinary, " +
            (snapshot ? "CAST(NULL AS binary(32))" : "SnapshotIdentityHash") +
            $" FROM dbo.{Table(snapshot)}{hint} WHERE IdentityHash = @Key;", connection, transaction);
        Bytes(command, "@Key", key, 32);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        if (reader.GetInt32(0) != 1 || reader.GetInt32(2) != 1) throw new NotSupportedException("Unsupported authority envelope.");
        var canonical = (byte[])reader.GetValue(1); var hash = (byte[])reader.GetValue(3); var payload = (byte[])reader.GetValue(4);
        if (!SHA256.HashData(canonical).AsSpan().SequenceEqual(key) || !canonical.AsSpan().SequenceEqual(payload) ||
            !SHA256.HashData(payload).AsSpan().SequenceEqual(hash)) throw new InvalidDataException("Authority envelope mismatch.");
        var value = AssumedBreakAuthorityV1Codec.Decode(payload, identity);
        if (snapshot != (value is AssumedBreakConfigurationSnapshot)) throw new InvalidDataException("Authority record kind mismatch.");
        if (value is AssumedBreakConfigurationAdmission admission &&
            !AssumedBreakAuthorityV1Codec.Key(admission.SnapshotIdentity, true).AsSpan().SequenceEqual((byte[])reader.GetValue(5)))
            throw new InvalidDataException("Admission snapshot binding mismatch.");
        return value;
    }
    private static string Table(bool snapshot) => snapshot ? "AssumedBreakConfigurationSnapshot" : "AssumedBreakConfigurationAdmission";
    private static void Bytes(SqlCommand command, string name, byte[] value, int size = -1) => command.Parameters.Add(name, SqlDbType.VarBinary, size).Value = value;
}
