using System.Collections.Immutable;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class AcquisitionEvidenceCodecTests
{
    internal static ObservationIngestionBatch Batch(ObservationStreamId? stream = null, Guid? id = null,
        ObservationCheckpoint? expected = null, ulong instance = 1, ulong requested = 1, ulong next = 10,
        ImmutableArray<AcquisitionRecoveryEvidence> recoveries = default, DateTimeOffset? contact = null)
    {
        stream ??= new(MachineId.New(), "mtconnect-test");
        return new(expected, new(stream, instance, next),
            [Observation(stream, requested), Observation(stream, next - 1)], contact ?? DateTimeOffset.UnixEpoch,
            new(id ?? Guid.NewGuid(), requested, 1, next, recoveries.IsDefault ? [] : recoveries));
    }
    private static SequencedMachineObservation Observation(ObservationStreamId stream, ulong sequence) => new(sequence,
        new MachineObservation { MachineId = stream.MachineId, Source = "mtconnect", Address = "execution", Type = SignalType.Enumeration,
            Quality = ObservationQuality.Good, Value = "ACTIVE", Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(sequence) });
    [Fact]
    public void FilteredSequencesRoundTripWithoutRequiringConsecutiveAgentObservations()
    {
        var batch = Batch(); var payload = AcquisitionEvidenceCodec.Encode(batch); var decoded = AcquisitionEvidenceCodec.Decode(payload);
        Assert.Equal(new ulong[] { 1, 9 }, decoded.Observations.Select(x => x.Sequence));
        Assert.Equal(payload, AcquisitionEvidenceCodec.Encode(decoded));
        Assert.Equal(AcquisitionEvidenceCodec.Identity(batch,payload),AcquisitionEvidenceCodec.Identity(decoded,payload));
    }
    [Fact]
    public void ChangedContentKeepsExplicitPublicationIdentityAndChangesPayload()
    {
        var first = Batch(); var changed = new ObservationIngestionBatch(first.ExpectedCheckpoint, first.Checkpoint, first.Observations,
            first.SuccessfulContactTime.AddSeconds(1), first.AcquisitionEvidence);
        var a=AcquisitionEvidenceCodec.Encode(first); var b=AcquisitionEvidenceCodec.Encode(changed);
        Assert.Equal(AcquisitionEvidenceCodec.Identity(first,a),AcquisitionEvidenceCodec.Identity(changed,b)); Assert.NotEqual(a,b);
    }
    [Fact]
    public void RecoveryChainMustConnectPriorCheckpointToResponseCursor()
    {
        var stream=new ObservationStreamId(MachineId.New(),"test"); var expected=new ObservationCheckpoint(stream,1,10);
        var loss=new AcquisitionRecoveryEvidence(AcquisitionRecoveryReason.InstanceChanged,1,2,10,20,"header","v1");
        var valid=Batch(stream,expected:expected,instance:2,requested:20,next:30,recoveries:[loss]);
        Assert.Single(AcquisitionEvidenceCodec.Decode(AcquisitionEvidenceCodec.Encode(valid)).AcquisitionEvidence!.Recoveries);
        Assert.Throws<ArgumentException>(()=>AcquisitionEvidenceCodec.Encode(Batch(stream,expected:expected,instance:2,requested:20,next:30)));
        Assert.Throws<ArgumentException>(()=>AcquisitionEvidenceCodec.Encode(Batch(stream,expected:expected,instance:2,requested:21,next:30,recoveries:[loss])));
    }
    [Fact]
    public void VersionTrailingAndMalformedBytesAreRejected()
    {
        var payload=AcquisitionEvidenceCodec.Encode(Batch()); var unknown=(byte[])payload.Clone(); unknown[0]=2;
        Assert.Throws<NotSupportedException>(()=>AcquisitionEvidenceCodec.Decode(unknown));
        Assert.Throws<InvalidDataException>(()=>AcquisitionEvidenceCodec.Decode([..payload,0]));
        Assert.Throws<ArgumentException>(()=>AcquisitionEvidenceCodec.Decode(payload[..5]));
    }
}
