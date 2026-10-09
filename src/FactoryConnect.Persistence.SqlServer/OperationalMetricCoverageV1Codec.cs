using System.Security.Cryptography;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>V1 little-endian primitives and exact UTF-16 code units; no text normalization.</summary>
internal static class OperationalMetricCoverageV1Codec
{
    public const int Version = 1;

    public static byte[] EncodeSubject(OperationalMetricCoverageSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0x31534346); // FCS1
        WriteString(writer, subject.ProcessorId.Value);
        var key = subject.EvaluationKey;
        writer.Write(key.MachineId.Value.ToByteArray());
        switch (key.PeriodId)
        {
            case OperationalMetricPeriodId.Shift shift:
                writer.Write((byte)1);
                WriteString(writer, shift.ShiftOccurrenceId.SiteId.Value);
                WriteString(writer, shift.ShiftOccurrenceId.ShiftScheduleAssignmentId.Value);
                WriteString(writer, shift.ShiftOccurrenceId.ShiftId.Value);
                writer.Write(shift.ShiftOccurrenceId.StartsAtUtc.UtcTicks);
                writer.Write(shift.ShiftOccurrenceId.EndsAtUtc.UtcTicks);
                break;
            case OperationalMetricPeriodId.ProductionDay day:
                writer.Write((byte)2);
                WriteString(writer, day.ProductionDayId.SiteId.Value);
                writer.Write(day.ProductionDayId.BusinessDate.DayNumber);
                break;
            default:
                throw new ArgumentException("Unsupported coverage period.", nameof(subject));
        }

        WriteOptionalString(writer, key.ContextKey.ProductionOrderId?.Value);
        WriteOptionalString(writer, key.ContextKey.OperationId?.Value);
        WriteOptionalString(writer, key.ContextKey.PartId?.Value);
        WriteOptionalString(writer, key.ContextKey.OperatorId?.Value);
        WriteString(writer, key.DefinitionId.MetricKey);
        WriteString(writer, key.DefinitionId.Version);
        WriteString(writer, subject.SourceRevision.ProcessorId.Value);
        writer.Write(subject.SourceRevision.StreamId.MachineId.Value.ToByteArray());
        WriteString(writer, subject.SourceRevision.StreamId.StreamKey);
        writer.Write(subject.SourceRevision.Position.Value);
        WriteString(writer, subject.CoveragePolicyVersion);
        return Finish(stream);
    }

    public static OperationalMetricCoverageSubject DecodeSubject(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream);
        Require(reader.ReadInt32() == 0x31534346);
        var processor = new OperationalMetricProjectionProcessorId(ReadString(reader));
        var machine = new MachineId(ReadGuid(reader));
        OperationalMetricPeriodId period = reader.ReadByte() switch
        {
            1 => new OperationalMetricPeriodId.Shift(new ShiftOccurrenceId(new SiteId(ReadString(reader)),
                new ShiftScheduleAssignmentId(ReadString(reader)), new ShiftId(ReadString(reader)), ReadUtc(reader), ReadUtc(reader))),
            2 => new OperationalMetricPeriodId.ProductionDay(new ProductionDayId(new SiteId(ReadString(reader)),
                DateOnly.FromDayNumber(reader.ReadInt32()))),
            _ => throw new InvalidDataException("Unsupported coverage period discriminator."),
        };
        var order = ReadOptionalString(reader);
        var operation = ReadOptionalString(reader);
        var part = ReadOptionalString(reader);
        var person = ReadOptionalString(reader);
        var context = new OperationalMetricEvaluationContextKey
        {
            ProductionOrderId = order is null ? null : new ProductionOrderId(order),
            OperationId = operation is null ? null : new OperationId(operation),
            PartId = part is null ? null : new PartId(part),
            OperatorId = person is null ? null : new OperatorId(person),
        };
        var definition = new OperationalMetricDefinitionId(ReadString(reader), ReadString(reader));
        var sourceProcessor = new MetricAggregationProcessorId(ReadString(reader));
        var sourceMachine = new MachineId(ReadGuid(reader));
        var sourceStream = new MetricInputStreamId(sourceMachine, ReadString(reader));
        var position = new MetricInputPosition(reader.ReadUInt64());
        var subject = new OperationalMetricCoverageSubject(processor,
            new OperationalMetricEvaluationKey(machine, period, definition, context),
            new MetricAggregationCheckpoint(sourceProcessor, sourceStream, position), ReadString(reader));
        Verify(bytes, stream, EncodeSubject(subject));
        return subject;
    }

    public static byte[] EncodeContent(OperationalMetricCoverageAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0x31434346); // FCC1
        ValidateEnums((int)assessment.Classification, (int)assessment.Reason);
        writer.Write((int)assessment.Classification);
        writer.Write((int)assessment.Reason);
        WriteNullableIntervals(writer, assessment.AssessedIntervals);
        writer.Write((byte)(assessment.CompletionBoundaryUtc is null ? 0 : 1));
        if (assessment.CompletionBoundaryUtc is { } boundary) { writer.Write(boundary.UtcTicks); }
        writer.Write((byte)(assessment.HistoricalScheduleAuthority is null ? 0 : 1));
        if (assessment.HistoricalScheduleAuthority is { } schedule)
        {
            WriteString(writer, schedule.AuthorityIdentity);
            WriteString(writer, schedule.Revision);
        }

        WriteNullableIntervals(writer, assessment.ExpectedIntervals);
        WriteIntervals(writer, assessment.ClassifiedIntervals);
        WriteIntervals(writer, assessment.GapIntervals);
        WriteIntervals(writer, assessment.UnknownEligibilityIntervals);
        writer.Write(assessment.EvidenceReferences.Count);
        foreach (var reference in assessment.EvidenceReferences)
        {
            WriteString(writer, reference.EvidenceKind);
            WriteString(writer, reference.Identity);
            WriteString(writer, reference.Revision);
        }

        return Finish(stream);
    }

    public static OperationalMetricCoverageAssessment DecodeContent(OperationalMetricCoverageSubject subject, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(bytes);
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream);
        Require(reader.ReadInt32() == 0x31434346);
        var classification = (OperationalMetricCoverageClassification)reader.ReadInt32();
        var reason = (OperationalMetricCoverageReason)reader.ReadInt32();
        ValidateEnums((int)classification, (int)reason);
        var assessed = ReadNullableIntervals(reader);
        DateTimeOffset? boundary = Presence(reader) ? ReadUtc(reader) : null;
        var schedule = Presence(reader)
            ? new OperationalMetricCoverageScheduleReference(ReadString(reader), ReadString(reader)) : null;
        var expected = ReadNullableIntervals(reader);
        var classified = ReadIntervals(reader);
        var gaps = ReadIntervals(reader);
        var unknown = ReadIntervals(reader);
        var count = ReadCount(reader, 12);
        var references = new OperationalMetricCoverageEvidenceReference[count];
        for (var index = 0; index < count; index++)
        {
            references[index] = new(ReadString(reader), ReadString(reader), ReadString(reader));
        }

        var assessment = new OperationalMetricCoverageAssessment(subject.ProcessorId, subject.EvaluationKey,
            subject.SourceRevision, subject.CoveragePolicyVersion, classification, reason, assessed, boundary,
            schedule, expected, classified, gaps, unknown, references);
        Verify(bytes, stream, EncodeContent(assessment));
        return assessment;
    }

    public static byte[] Hash(byte[] bytes) => SHA256.HashData(bytes);

    private static void WriteString(BinaryWriter writer, string value)
    {
        var byteCount = checked((long)value.Length * 2);
        EnsureCapacity(writer.BaseStream, checked(byteCount + 4));
        writer.Write(value.Length);
        foreach (var character in value) { writer.Write((ushort)character); }
    }

    private static string ReadString(BinaryReader reader)
    {
        var count = ReadCount(reader, 2);
        var characters = new char[count];
        for (var index = 0; index < count; index++) { characters[index] = (char)reader.ReadUInt16(); }
        return new string(characters);
    }

    private static void WriteOptionalString(BinaryWriter writer, string? value)
    {
        writer.Write((byte)(value is null ? 0 : 1));
        if (value is not null) { WriteString(writer, value); }
    }

    private static string? ReadOptionalString(BinaryReader reader) => Presence(reader) ? ReadString(reader) : null;
    private static bool Presence(BinaryReader reader) => reader.ReadByte() switch
    {
        0 => false,
        1 => true,
        _ => throw new InvalidDataException("Invalid presence marker."),
    };

    private static void WriteNullableIntervals(BinaryWriter writer, IReadOnlyList<OperationalMetricCoverageInterval>? intervals)
    {
        writer.Write((byte)(intervals is null ? 0 : 1));
        if (intervals is not null) { WriteIntervals(writer, intervals); }
    }

    private static void WriteIntervals(BinaryWriter writer, IReadOnlyList<OperationalMetricCoverageInterval> intervals)
    {
        EnsureCapacity(writer.BaseStream, checked(4L + (16L * intervals.Count)));
        writer.Write(intervals.Count);
        foreach (var interval in intervals)
        {
            writer.Write(interval.StartsAtUtc.UtcTicks);
            writer.Write(interval.EndsAtUtc.UtcTicks);
        }
    }

    private static OperationalMetricCoverageInterval[]? ReadNullableIntervals(BinaryReader reader) => Presence(reader) ? ReadIntervals(reader) : null;
    private static OperationalMetricCoverageInterval[] ReadIntervals(BinaryReader reader)
    {
        var count = ReadCount(reader, 16);
        var result = new OperationalMetricCoverageInterval[count];
        for (var index = 0; index < count; index++) { result[index] = new(ReadUtc(reader), ReadUtc(reader)); }
        return result;
    }

    private static int ReadCount(BinaryReader reader, int minimumBytes)
    {
        var count = reader.ReadInt32();
        Require(count >= 0 && checked((long)count * minimumBytes) <= reader.BaseStream.Length - reader.BaseStream.Position);
        return count;
    }

    private static DateTimeOffset ReadUtc(BinaryReader reader) => new(reader.ReadInt64(), TimeSpan.Zero);
    private static Guid ReadGuid(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(16);
        Require(bytes.Length == 16);
        return new Guid(bytes);
    }

    private static void Verify(byte[] bytes, Stream stream, byte[] canonical) =>
        Require(stream.Position == stream.Length && bytes.AsSpan().SequenceEqual(canonical));
    private static void ValidateEnums(int classification, int reason) =>
        Require(classification is >= 0 and <= 2 && reason is >= 0 and <= 5);

    private static void Require(bool condition)
    {
        if (!condition) { throw new InvalidDataException("Malformed or noncanonical coverage V1 bytes."); }
    }

    private static void EnsureCapacity(Stream stream, long additional) =>
        ArgumentOutOfRangeException.ThrowIfGreaterThan(checked(stream.Length + additional), Array.MaxLength);
    private static byte[] Finish(MemoryStream stream)
    {
        EnsureCapacity(stream, 0);
        return stream.ToArray();
    }
}
