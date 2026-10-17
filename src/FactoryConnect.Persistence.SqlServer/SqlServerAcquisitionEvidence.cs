using System.Data;
using System.Security.Cryptography;
using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

public sealed class SqlServerAcquisitionEvidenceReader(string connectionString) : IAcquisitionEvidenceReader
{
    public ValueTask<AcquisitionRecoveryReadResult> ReadRecoveryExactAsync(ObservationStreamId stream, string publicationIdentity,
        string recoveryIdentity, CancellationToken cancellationToken = default) =>
        new SqlServerObservationIngestionStore(connectionString).ReadRecoveryExactAsync(stream, publicationIdentity, recoveryIdentity, cancellationToken);

    public ValueTask<AcquisitionEvidenceReadResult> ReadExactAsync(ObservationStreamId stream, string identity, CancellationToken cancellationToken = default) =>
        new SqlServerObservationIngestionStore(connectionString).ReadExactAsync(stream, identity, cancellationToken);
}

internal sealed partial class SqlServerObservationIngestionStore
{
    public async ValueTask<AcquisitionRecoveryReadResult> ReadRecoveryExactAsync(ObservationStreamId stream, string publicationIdentity,
        string recoveryIdentity, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryIdentity);
        var read = await ReadExactAsync(stream, publicationIdentity, cancellationToken);
        if (read.Status != AcquisitionEvidenceReadStatus.Found) return new(read.Status);
        var index = read.Record!.RecoveryIdentities.IndexOf(recoveryIdentity);
        return index < 0 ? new(AcquisitionEvidenceReadStatus.Absent) :
            new(AcquisitionEvidenceReadStatus.Found, read.Record.Batch.AcquisitionEvidence!.Recoveries[index], read.Record.PublicationOrdinal, index);
    }

    public async ValueTask<AcquisitionEvidenceReadResult> ReadExactAsync(ObservationStreamId stream, string identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream); ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        var key = Convert.FromHexString(identity); if (key.Length != 32) throw new ArgumentException("Invalid acquisition identity.", nameof(identity));
        await using var c = new SqlConnection(ConnectionString); await c.OpenAsync(cancellationToken);
        await using var t = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var result = await ReadEvidenceAsync(c, t, stream, key, cancellationToken);
            await t.CommitAsync(cancellationToken); return result;
        }
        catch (NotSupportedException) { return new(AcquisitionEvidenceReadStatus.UnsupportedEncoding); }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or EndOfStreamException or OverflowException)
        { return new(AcquisitionEvidenceReadStatus.Corrupt); }
    }
    private static async Task<bool> ValidateEvidenceReplayAsync(SqlConnection c, SqlTransaction t, ObservationStreamId stream,
        byte[] identity, byte[] payload, CancellationToken token)
    {
        var result = await ReadEvidenceAsync(c, t, stream, identity, token);
        if (result.Status == AcquisitionEvidenceReadStatus.Absent) return false;
        if (result.Status != AcquisitionEvidenceReadStatus.Found || !AcquisitionEvidenceCodec.Encode(result.Record!.Batch).AsSpan().SequenceEqual(payload))
            throw new InvalidOperationException("Acquisition publication conflicts with immutable evidence.");
        return true;
    }
    private static async Task<AcquisitionEvidenceReadResult> ReadEvidenceAsync(SqlConnection c, SqlTransaction t,
        ObservationStreamId stream, byte[] identity, CancellationToken token)
    {
        await using var command = c.CreateCommand(); command.Transaction = t;
        command.CommandText = """
            SELECT IdentityHash, PublicationOrdinal, PreviousIdentity, AcceptedBefore, AcceptedThrough, PayloadVersion, ContentHash, PayloadBinary
            FROM dbo.AcquisitionEvidencePublication
            WHERE MachineId=@MachineId AND StreamKeyBinary=@StreamKeyBinary AND PublicationOrdinal <=
                (SELECT PublicationOrdinal FROM dbo.AcquisitionEvidencePublication
                 WHERE MachineId=@MachineId AND StreamKeyBinary=@StreamKeyBinary AND IdentityHash=@Identity)
            ORDER BY PublicationOrdinal;
            """;
        AddStreamIdentityParameters(command, stream, OrdinalStringKeyCodec.Encode(stream.StreamKey)); command.Parameters.Add("@Identity", SqlDbType.Binary, 32).Value = identity;
        await using var reader = await command.ExecuteReaderAsync(token);
        AcquisitionEvidenceRecord? found = null; byte[]? previous = null; long ordinal = 0; ObservationPosition? lastThrough = null; ObservationCheckpoint? lastCheckpoint = null;
        while (await reader.ReadAsync(token))
        {
            var id = (byte[])reader[0]; var n = reader.GetInt64(1); var prior = reader.IsDBNull(2) ? null : (byte[])reader[2];
            var before = reader.IsDBNull(3) ? null : new ObservationPosition(SqlServerUInt64.Materialize(reader.GetDecimal(3)));
            var through = reader.IsDBNull(4) ? null : new ObservationPosition(SqlServerUInt64.Materialize(reader.GetDecimal(4)));
            if (reader.GetInt32(5) != AcquisitionEvidenceCodec.Version) return new(AcquisitionEvidenceReadStatus.UnsupportedEncoding);
            var payload = (byte[])reader[7]; var batch = AcquisitionEvidenceCodec.Decode(payload);
            if (n != ++ordinal || !SameBytes(previous, prior) || batch.Checkpoint.StreamId != stream ||
                !id.AsSpan().SequenceEqual(AcquisitionEvidenceCodec.Identity(batch, payload)) ||
                !((byte[])reader[6]).AsSpan().SequenceEqual(EvidenceHash(payload, n, prior, before, through)) ||
                (n > 1 && (before != lastThrough || batch.ExpectedCheckpoint != lastCheckpoint)) || (before?.Value ?? 0) > (through?.Value ?? 0))
                return new(AcquisitionEvidenceReadStatus.Corrupt);
            found = new(Convert.ToHexString(id), n, prior is null ? null : Convert.ToHexString(prior), batch, before, through,
                AcquisitionEvidenceCodec.RecoveryIdentities(id, batch));
            previous = id; lastThrough = through; lastCheckpoint = batch.Checkpoint;
        }
        return found is null ? new(AcquisitionEvidenceReadStatus.Absent) : new(AcquisitionEvidenceReadStatus.Found, found);
    }
    private static bool SameBytes(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);
    private static byte[] EvidenceHash(byte[] payload, long ordinal, byte[]? previous, ObservationPosition? before, ObservationPosition? through)
    {
        using var b = new MemoryStream(); using var w = new BinaryWriter(b);
        w.Write("acquisition-envelope/v1"); w.Write(ordinal); w.Write(previous is not null); if (previous is not null) w.Write(previous);
        w.Write(before is not null); if (before is not null) w.Write(before.Value);
        w.Write(through is not null); if (through is not null) w.Write(through.Value);
        w.Write(payload.Length); w.Write(payload); w.Flush(); return SHA256.HashData(b.ToArray());
    }
    private static async Task AppendEvidenceAsync(SqlConnection c, SqlTransaction t, ObservationIngestionBatch batch,
        byte[] identity, byte[] payload, ObservationPosition? before, ObservationPosition? through, CancellationToken token)
    {
        long ordinal = 1; byte[]? previous = null;
        await using (var head = c.CreateCommand())
        {
            head.Transaction = t; head.CommandText = """
                SELECT p.PublicationOrdinal, p.IdentityHash
                FROM dbo.AcquisitionEvidenceHead h WITH (UPDLOCK,HOLDLOCK)
                JOIN dbo.AcquisitionEvidencePublication p ON p.MachineId=h.MachineId AND p.StreamKeyBinary=h.StreamKeyBinary AND p.IdentityHash=h.IdentityHash
                WHERE h.MachineId=@MachineId AND h.StreamKeyBinary=@StreamKeyBinary;
                """;
            AddStreamIdentityParameters(head, batch.Checkpoint.StreamId, OrdinalStringKeyCodec.Encode(batch.Checkpoint.StreamId.StreamKey));
            await using var reader = await head.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token)) { ordinal = checked(reader.GetInt64(0) + 1); previous = (byte[])reader[1]; }
        }
        if (previous is not null)
        {
            var prior = await ReadEvidenceAsync(c, t, batch.Checkpoint.StreamId, previous, token);
            if (prior.Status != AcquisitionEvidenceReadStatus.Found || prior.Record!.AcceptedThrough != before || prior.Record.Batch.Checkpoint != batch.ExpectedCheckpoint)
                throw new InvalidDataException("Acquisition history cannot be extended from invalid authority.");
        }
        await using var insert = c.CreateCommand(); insert.Transaction = t; insert.CommandText = """
            INSERT dbo.AcquisitionEvidencePublication
                (MachineId,StreamKeyBinary,IdentityHash,PublicationOrdinal,PreviousIdentity,AcceptedBefore,AcceptedThrough,PayloadVersion,ContentHash,PayloadBinary)
            VALUES (@MachineId,@StreamKeyBinary,@Identity,@Ordinal,@Previous,@Before,@Through,1,@Hash,@Payload);
            """;
        AddStreamIdentityParameters(insert, batch.Checkpoint.StreamId, OrdinalStringKeyCodec.Encode(batch.Checkpoint.StreamId.StreamKey));
        insert.Parameters.Add("@Identity", SqlDbType.Binary,32).Value=identity; insert.Parameters.Add("@Ordinal",SqlDbType.BigInt).Value=ordinal;
        insert.Parameters.Add("@Previous",SqlDbType.Binary,32).Value=(object?)previous ?? DBNull.Value;
        AddPosition(insert,"@Before",before); AddPosition(insert,"@Through",through);
        insert.Parameters.Add("@Hash",SqlDbType.Binary,32).Value=EvidenceHash(payload,ordinal,previous,before,through);
        insert.Parameters.Add("@Payload",SqlDbType.VarBinary,-1).Value=payload; await insert.ExecuteNonQueryAsync(token);
        await using var advance=c.CreateCommand(); advance.Transaction=t;
        advance.CommandText=previous is null ?
            "INSERT dbo.AcquisitionEvidenceHead (MachineId,StreamKeyBinary,IdentityHash) VALUES (@MachineId,@StreamKeyBinary,@Identity);" :
            "UPDATE dbo.AcquisitionEvidenceHead SET IdentityHash=@Identity WHERE MachineId=@MachineId AND StreamKeyBinary=@StreamKeyBinary;";
        AddStreamIdentityParameters(advance,batch.Checkpoint.StreamId,OrdinalStringKeyCodec.Encode(batch.Checkpoint.StreamId.StreamKey));
        advance.Parameters.Add("@Identity",SqlDbType.Binary,32).Value=identity; await advance.ExecuteNonQueryAsync(token);
    }
    private static void AddPosition(SqlCommand c, string name, ObservationPosition? value)
    {
        var p=c.Parameters.Add(name,SqlDbType.Decimal); p.Precision=20; p.Scale=0; p.Value=value is null ? DBNull.Value : (decimal)value.Value;
    }
}
