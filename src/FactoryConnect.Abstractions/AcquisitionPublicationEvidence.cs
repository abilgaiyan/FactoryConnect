using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace FactoryConnect.Abstractions;

public enum AcquisitionRecoveryReason { OutOfRange, InstanceChanged }

public sealed record AcquisitionRecoveryEvidence(
    AcquisitionRecoveryReason Reason, ulong? PreviousInstanceId, ulong CurrentInstanceId,
    ulong PreviousSequence, ulong RecoverySequence, string SourceReference, string PolicyVersion);

// Header limits describe the Agent buffer; RequestedSequence/NextSequence describe the response cursor.
public sealed record AcquisitionResponseEvidence(
    Guid PublicationId, ulong RequestedSequence, ulong FirstSequence, ulong LastSequence,
    ImmutableArray<AcquisitionRecoveryEvidence> Recoveries);

public enum AcquisitionOpeningBoundaryKind { NoPriorDurablePrefix, UnverifiedLegacyPrefix }

// NoPriorDurablePrefix describes SQL history only; it is not proof of complete acquisition.
public sealed record AcquisitionOpeningBoundary(AcquisitionOpeningBoundaryKind Kind,
    ObservationPosition? LegacyAcceptedThrough, ObservationCheckpoint? LegacyCheckpoint);

public sealed record AcquisitionEvidenceRecord(
    string Identity, long PublicationOrdinal, string? PreviousIdentity,
    ObservationIngestionBatch Batch, ObservationPosition? AcceptedBefore,
    ObservationPosition? AcceptedThrough, ImmutableArray<string> RecoveryIdentities, AcquisitionOpeningBoundary OpeningBoundary);

public enum AcquisitionEvidenceReadStatus { Absent, Found, Corrupt, UnsupportedEncoding }
public sealed record AcquisitionEvidenceReadResult(AcquisitionEvidenceReadStatus Status, AcquisitionEvidenceRecord? Record = null);

public sealed record AcquisitionRecoveryReadResult(AcquisitionEvidenceReadStatus Status, AcquisitionRecoveryEvidence? Evidence = null,
    long? PublicationOrdinal = null, int? EventOrdinal = null);

public interface IAcquisitionEvidenceReader
{
    ValueTask<AcquisitionRecoveryReadResult> ReadRecoveryExactAsync(ObservationStreamId stream, string publicationIdentity, string recoveryIdentity,
        CancellationToken cancellationToken = default);

    ValueTask<AcquisitionEvidenceReadResult> ReadExactAsync(ObservationStreamId stream, string identity,
        CancellationToken cancellationToken = default);
}

public static class AcquisitionPublicationIdentity
{
    public static string Create(ObservationStreamId stream, Guid publicationId)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (publicationId == Guid.Empty) throw new ArgumentException("Publication ID must not be empty.", nameof(publicationId));
        using var buffer = new MemoryStream(); using var writer = new BinaryWriter(buffer, new UTF8Encoding(false, true));
        writer.Write("acquisition-publication/v1"); writer.Write(stream.MachineId.Value.ToByteArray()); writer.Write(stream.StreamKey);
        writer.Write(publicationId.ToByteArray()); writer.Flush(); return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
    }
}
