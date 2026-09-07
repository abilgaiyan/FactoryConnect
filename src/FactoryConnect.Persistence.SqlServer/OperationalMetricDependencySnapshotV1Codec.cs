using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>
/// Writes the frozen FC-030.2A DependencySnapshotBinary V1 (FCDS) representation.
/// </summary>
internal static class OperationalMetricDependencySnapshotV1Codec
{
    public const short CodecVersion = 1;
    public const int MaximumEncodedLength = 16_777_216;
    private const int MaximumDepth = 64;
    private const int MaximumEvidenceItemsPerKind = 65_535;
    private const int MaximumTotalEvidenceNodeCount = 262_144;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Encode(OperationalMetricProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);

        var state = new EncodingState();
        var encoded = EncodeProjection(projection, depth: 1, state);
        if (encoded.Length > MaximumEncodedLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(projection),
                "Operational metric dependency snapshot exceeds the frozen V1 maximum encoded length.");
        }

        return encoded;
    }

    public static byte[] ComputeHash(ReadOnlySpan<byte> encodedSnapshot) =>
        SHA256.HashData(encodedSnapshot);

    private static byte[] EncodeProjection(
        OperationalMetricProjection projection,
        int depth,
        EncodingState state)
    {
        if (depth > MaximumDepth)
        {
            throw new ArgumentOutOfRangeException(
                nameof(projection),
                "Operational metric dependency snapshot exceeds the frozen V1 maximum recursive depth.");
        }

        if (projection.OperandEvidence.Count > MaximumEvidenceItemsPerKind ||
            projection.DependencyEvidence.Count > MaximumEvidenceItemsPerKind)
        {
            throw new ArgumentOutOfRangeException(
                nameof(projection),
                "Operational metric dependency snapshot exceeds the frozen V1 per-kind evidence count.");
        }

        using var stream = new MemoryStream();
        stream.Write("FCDS"u8);
        stream.WriteByte(0x02);
        WriteUInt16(stream, CodecVersion);

        WriteRequiredString(stream, projection.ProcessorId.Value);
        WriteBlob(stream, OperationalMetricEvaluationKeyV1Codec.Encode(projection.Key));
        WriteStatus(stream, projection.Status);
        WriteOptionalDecimal(stream, projection.Value);
        WriteRequiredString(stream, projection.Unit);
        WriteOptionalReason(stream, projection.ReasonCode);
        WriteOptionalString(stream, projection.ReasonOperandName);
        WriteSourceRevision(stream, projection.SourceRevision);

        WriteUInt32(stream, checked((uint)projection.OperandEvidence.Count));
        foreach (var evidence in projection.OperandEvidence)
        {
            state.AddEvidenceNode();
            WriteComponentEvidence(stream, evidence);
        }

        WriteUInt32(stream, checked((uint)projection.DependencyEvidence.Count));
        foreach (var evidence in projection.DependencyEvidence)
        {
            state.AddEvidenceNode();
            WriteDependencyEvidence(stream, evidence, depth, state);
        }

        if (stream.Length > MaximumEncodedLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(projection),
                "Operational metric dependency snapshot exceeds the frozen V1 maximum encoded length.");
        }

        return stream.ToArray();
    }

    private static void WriteComponentEvidence(
        Stream stream,
        OperationalMetricComponentProjectionEvidence evidence)
    {
        WriteRequiredString(stream, evidence.OperandName);
        WriteGuid(stream, evidence.SourceIdentity.MachineId.Value);
        WritePeriod(stream, evidence.SourceIdentity.PeriodId);
        WriteRequiredString(stream, evidence.SourceIdentity.ComponentKey);
        WriteSourceRevision(stream, evidence.SourceRevision);

        var dimension = checked((byte)evidence.Dimension);
        if (dimension > 2)
        {
            throw new InvalidOperationException("Unsupported MetricDimension for DependencySnapshotBinary V1.");
        }
        stream.WriteByte(dimension);

        WriteRequiredString(stream, CanonicalDecimalTextV1Codec.Serialize(evidence.Value));
        WriteRequiredString(stream, evidence.Unit);
        WriteUInt64(stream, checked((ulong)evidence.InputCount));
        WriteUtcTimestamp(stream, evidence.FirstInputTimestamp);
        WriteUtcTimestamp(stream, evidence.LastInputTimestamp);
    }

    private static void WriteDependencyEvidence(
        Stream stream,
        OperationalMetricDependencyProjectionEvidence evidence,
        int parentDepth,
        EncodingState state)
    {
        WriteRequiredString(stream, evidence.OperandName);
        WriteDefinitionId(stream, evidence.DefinitionId);
        var nested = EncodeProjection(evidence.Projection, checked(parentDepth + 1), state);
        WriteBlob(stream, nested);
    }

    private static void WriteSourceRevision(Stream stream, MetricAggregationCheckpoint revision)
    {
        WriteRequiredString(stream, revision.ProcessorId.Value);
        WriteGuid(stream, revision.StreamId.MachineId.Value);
        WriteRequiredString(stream, revision.StreamId.StreamKey);
        WriteUInt64(stream, revision.Position.Value);
    }

    private static void WriteDefinitionId(Stream stream, OperationalMetricDefinitionId definitionId)
    {
        WriteRequiredString(stream, definitionId.MetricKey);
        WriteRequiredString(stream, definitionId.Version);
    }

    private static void WritePeriod(Stream stream, OperationalMetricPeriodId periodId)
    {
        switch (periodId)
        {
            case OperationalMetricPeriodId.Shift shift:
                stream.WriteByte(0x01);
                WriteRequiredString(stream, shift.ShiftOccurrenceId.SiteId.Value);
                WriteRequiredString(stream, shift.ShiftOccurrenceId.ShiftScheduleAssignmentId.Value);
                WriteRequiredString(stream, shift.ShiftOccurrenceId.ShiftId.Value);
                WriteUtcTimestamp(stream, shift.ShiftOccurrenceId.StartsAtUtc);
                WriteUtcTimestamp(stream, shift.ShiftOccurrenceId.EndsAtUtc);
                break;

            case OperationalMetricPeriodId.ProductionDay productionDay:
                stream.WriteByte(0x02);
                WriteRequiredString(stream, productionDay.ProductionDayId.SiteId.Value);
                WriteInt32(stream, productionDay.ProductionDayId.BusinessDate.DayNumber);
                break;

            default:
                throw new InvalidOperationException("Unsupported operational metric period type for DependencySnapshotBinary V1.");
        }
    }

    private static void WriteStatus(Stream stream, OperationalMetricEvaluationStatus status)
    {
        var value = checked((byte)status);
        if (value > 2)
        {
            throw new InvalidOperationException("Unsupported operational metric status for DependencySnapshotBinary V1.");
        }
        stream.WriteByte(value);
    }

    private static void WriteOptionalDecimal(Stream stream, decimal? value)
    {
        if (value is null)
        {
            stream.WriteByte(0x00);
            return;
        }

        stream.WriteByte(0x01);
        WriteRequiredString(stream, CanonicalDecimalTextV1Codec.Serialize(value.Value));
    }

    private static void WriteOptionalReason(
        Stream stream,
        OperationalMetricEvaluationReasonCode? reasonCode)
    {
        if (reasonCode is null)
        {
            stream.WriteByte(0x00);
            return;
        }

        var value = checked((byte)reasonCode.Value);
        if (value > 5)
        {
            throw new InvalidOperationException("Unsupported operational metric reason code for DependencySnapshotBinary V1.");
        }

        stream.WriteByte(0x01);
        stream.WriteByte(value);
    }

    private static void WriteRequiredString(Stream stream, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        WriteString(stream, value);
    }

    private static void WriteOptionalString(Stream stream, string? value)
    {
        if (value is null)
        {
            stream.WriteByte(0x00);
            return;
        }

        if (value.Length == 0)
        {
            throw new ArgumentException("Present canonical identity strings must not be empty.", nameof(value));
        }

        stream.WriteByte(0x01);
        WriteString(stream, value);
    }

    private static void WriteString(Stream stream, string value)
    {
        if (value.Length > 256)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "CanonicalStringV1 is limited to 256 UTF-16 code units.");
        }

        byte[] utf8;
        try
        {
            utf8 = StrictUtf8.GetBytes(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "CanonicalStringV1 requires well-formed UTF-16 input.",
                nameof(value),
                exception);
        }

        if (utf8.Length > 768)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "CanonicalStringV1 UTF-8 payload is limited to 768 bytes.");
        }

        WriteUInt32(stream, checked((uint)utf8.Length));
        stream.Write(utf8);
    }

    private static void WriteBlob(Stream stream, byte[] bytes)
    {
        WriteUInt32(stream, checked((uint)bytes.Length));
        stream.Write(bytes);
    }

    private static void WriteGuid(Stream stream, Guid value)
    {
        Span<byte> buffer = stackalloc byte[16];
        if (!value.TryWriteBytes(buffer, bigEndian: true, out var written) || written != buffer.Length)
        {
            throw new InvalidOperationException("Could not encode Guid using the frozen V1 field order.");
        }
        stream.Write(buffer);
    }

    private static void WriteUtcTimestamp(Stream stream, DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Canonical UTC timestamps require a zero offset.", nameof(value));
        }
        WriteInt64(stream, value.UtcDateTime.Ticks);
    }

    private static void WriteUInt16(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, checked((ushort)value));
        stream.Write(buffer);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteUInt64(Stream stream, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private sealed class EncodingState
    {
        private int _totalEvidenceNodeCount;

        public void AddEvidenceNode()
        {
            _totalEvidenceNodeCount = checked(_totalEvidenceNodeCount + 1);
            if (_totalEvidenceNodeCount > MaximumTotalEvidenceNodeCount)
            {
                throw new ArgumentOutOfRangeException(
                    "projection",
                    "Operational metric dependency snapshot exceeds the frozen V1 total evidence node count.");
            }
        }
    }
}
