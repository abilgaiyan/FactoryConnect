using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Persistence.SqlServer;

internal static class AcquisitionEvidenceCodec
{
    internal const int Version = 1;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static byte[] Encode(ObservationIngestionBatch batch)
    {
        using var buffer = new MemoryStream(); using var w = new BinaryWriter(buffer, Utf8, true);
        w.Write(Version); w.Write(batch.Checkpoint.StreamId.MachineId.Value.ToByteArray()); w.Write(batch.Checkpoint.StreamId.StreamKey);
        w.Write(batch.ExpectedCheckpoint is not null);
        if (batch.ExpectedCheckpoint is { } prior) { w.Write(prior.InstanceId); w.Write(prior.NextSequence); }
        w.Write(batch.Checkpoint.InstanceId); w.Write(batch.Checkpoint.NextSequence); w.Write(batch.SuccessfulContactTime.UtcTicks);
        w.Write(batch.AcquisitionEvidence is not null);
        if (batch.AcquisitionEvidence is { } e)
        {
            Validate(batch, e);
            w.Write(e.PublicationId.ToByteArray()); w.Write(e.RequestedSequence); w.Write(e.FirstSequence); w.Write(e.LastSequence);
            w.Write(e.Recoveries.Length);
            foreach (var loss in e.Recoveries)
            {
                w.Write((int)loss.Reason); w.Write(loss.PreviousInstanceId.HasValue);
                if (loss.PreviousInstanceId.HasValue) w.Write(loss.PreviousInstanceId.Value);
                w.Write(loss.CurrentInstanceId); w.Write(loss.PreviousSequence); w.Write(loss.RecoverySequence);
                w.Write(loss.SourceReference); w.Write(loss.PolicyVersion);
            }
        }
        var observations = batch.Observations.OrderBy(x => x.Sequence).ToArray(); w.Write(observations.Length);
        foreach (var item in observations)
        {
            var o = item.Observation;
            w.Write(item.Sequence); w.Write(o.Source); w.Write(o.Address); w.Write((int)o.Type); w.Write((int)o.Quality); w.Write(o.Timestamp.UtcTicks);
            var value = SqlServerObservationValueCodec.Serialize(o.Type, o.Value); w.Write(value is not null); if (value is not null) w.Write(value);
        }
        w.Flush(); if (buffer.Length > 16777216) throw new ArgumentException("Acquisition evidence is too large.");
        return buffer.ToArray();
    }
    public static ObservationIngestionBatch Decode(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length > 16777216) throw new InvalidDataException("Acquisition evidence is too large.");
        using var buffer = new MemoryStream(payload, false); using var r = new BinaryReader(buffer, Utf8);
        if (r.ReadInt32() != Version) throw new NotSupportedException("Unsupported acquisition evidence encoding.");
        var stream = new ObservationStreamId(new MachineId(new Guid(r.ReadBytes(16))), r.ReadString());
        ObservationCheckpoint? expected = r.ReadBoolean() ? new(stream, r.ReadUInt64(), r.ReadUInt64()) : null;
        var next = new ObservationCheckpoint(stream, r.ReadUInt64(), r.ReadUInt64()); var contact = new DateTimeOffset(r.ReadInt64(), TimeSpan.Zero);
        AcquisitionResponseEvidence? evidence = null;
        if (r.ReadBoolean())
        {
            var id = new Guid(r.ReadBytes(16)); var requested = r.ReadUInt64(); var first = r.ReadUInt64(); var last = r.ReadUInt64();
            var losses = ImmutableArray.CreateBuilder<AcquisitionRecoveryEvidence>(); var count = Count(r);
            for (var i = 0; i < count; i++) losses.Add(new((AcquisitionRecoveryReason)r.ReadInt32(), r.ReadBoolean() ? r.ReadUInt64() : null,
                r.ReadUInt64(), r.ReadUInt64(), r.ReadUInt64(), r.ReadString(), r.ReadString()));
            evidence = new(id, requested, first, last, losses.ToImmutable());
        }
        var items = new List<SequencedMachineObservation>(); var length = Count(r);
        for (var i = 0; i < length; i++)
        {
            var sequence = r.ReadUInt64(); var source = r.ReadString(); var address = r.ReadString(); var type = (SignalType)r.ReadInt32();
            var quality = (ObservationQuality)r.ReadInt32(); var timestamp = new DateTimeOffset(r.ReadInt64(), TimeSpan.Zero);
            var value = r.ReadBoolean() ? r.ReadString() : null;
            if (!Enum.IsDefined(type) || !Enum.IsDefined(quality)) throw new InvalidDataException("Invalid observation enum.");
            items.Add(new(sequence, new MachineObservation { MachineId = stream.MachineId, Source = source, Address = address, Type = type,
                Quality = quality, Timestamp = timestamp, Value = SqlServerObservationValueCodec.Deserialize(type, value) }));
        }
        var result = new ObservationIngestionBatch(expected, next, items, contact, evidence);
        if (buffer.Position != buffer.Length || !Encode(result).AsSpan().SequenceEqual(payload)) throw new InvalidDataException("Noncanonical acquisition evidence.");
        return result;
    }
    private static int Count(BinaryReader r) { var n = r.ReadInt32(); if (n < 0 || n > 1000000) throw new InvalidDataException("Invalid evidence count."); return n; }
    public static byte[] Identity(ObservationIngestionBatch batch, byte[] payload)
    {
        if (batch.AcquisitionEvidence is null) return SHA256.HashData(payload);
        return Convert.FromHexString(AcquisitionPublicationIdentity.Create(batch.Checkpoint.StreamId, batch.AcquisitionEvidence.PublicationId));
    }
    public static ImmutableArray<string> RecoveryIdentities(byte[] identity, ObservationIngestionBatch batch)
    {
        var result = ImmutableArray.CreateBuilder<string>();
        for (var i = 0; i < (batch.AcquisitionEvidence?.Recoveries.Length ?? 0); i++)
        {
            using var b = new MemoryStream(); using var w = new BinaryWriter(b, Utf8); w.Write("acquisition-recovery/v1"); w.Write(identity); w.Write(i); w.Flush();
            result.Add(Convert.ToHexString(SHA256.HashData(b.ToArray())));
        }
        return result.ToImmutable();
    }
    private static void Validate(ObservationIngestionBatch batch, AcquisitionResponseEvidence e)
    {
        if (e.PublicationId == Guid.Empty || e.Recoveries.IsDefault || e.LastSequence == ulong.MaxValue || e.FirstSequence > e.LastSequence + 1 ||
            e.RequestedSequence < e.FirstSequence || batch.Checkpoint.NextSequence < e.RequestedSequence || batch.Checkpoint.NextSequence > e.LastSequence + 1)
            throw new ArgumentException("Invalid acquisition response window.");
        ulong? instance = batch.ExpectedCheckpoint?.InstanceId; var cursor = batch.ExpectedCheckpoint?.NextSequence;
        foreach (var loss in e.Recoveries)
        {
            if (!Enum.IsDefined(loss.Reason) || (loss.PreviousInstanceId.HasValue && loss.PreviousInstanceId != instance) || (cursor.HasValue && cursor != loss.PreviousSequence))
                throw new ArgumentException("Recovery evidence does not continue the prior boundary.");
            ArgumentException.ThrowIfNullOrWhiteSpace(loss.SourceReference); ArgumentException.ThrowIfNullOrWhiteSpace(loss.PolicyVersion);
            instance = loss.CurrentInstanceId; cursor = loss.RecoverySequence;
        }
        if (e.Recoveries.Length > 0 && (instance != batch.Checkpoint.InstanceId || cursor != e.RequestedSequence))
            throw new ArgumentException("Recovery evidence does not reach the accepted response.");
        if (e.Recoveries.Length == 0 && batch.ExpectedCheckpoint is { } expected &&
            (expected.InstanceId != batch.Checkpoint.InstanceId || expected.NextSequence != e.RequestedSequence))
            throw new ArgumentException("Acquisition requires an explicit recovery boundary.");
        foreach (var item in batch.Observations)
            if (item.Sequence < e.RequestedSequence || item.Sequence >= batch.Checkpoint.NextSequence)
                throw new ArgumentException("Observation is outside the response cursor window.");
    }
}
