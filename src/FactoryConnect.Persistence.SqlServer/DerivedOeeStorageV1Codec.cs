using System.Globalization;
using System.Text;
using FactoryConnect.Abstractions;
using FactoryConnect.Core.Oee;

namespace FactoryConnect.Persistence.SqlServer;

internal static class DerivedOeeStorageV1Codec
{
    internal const int Version = 1;
    internal const int MaximumLength = 16_777_216;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static byte[] Encode(DerivedOeeAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Utf8, true))
        {
            writer.Write("FactoryConnect.DerivedOeePayload"); writer.Write(Version);
            writer.Write(assessment.Identity); writer.Write(assessment.ContentFingerprint);
            Blob(writer, QualityStorageV1Codec.EncodeAssessment(assessment.Target.Quality));
            Blob(writer, OperationalMetricDependencySnapshotV1Codec.Encode(assessment.Target.Availability.Projection));
            Blob(writer, OperationalMetricDependencySnapshotV1Codec.Encode(assessment.Target.Performance.Projection));
            var cut = assessment.Target.Cut;
            writer.Write(cut.Decision.Authority); writer.Write(cut.Decision.Identity); writer.Write(cut.Decision.Revision);
            writer.Write((int)cut.State); writer.Write(cut.ProjectionProcessor.Value);
            writer.Write(cut.MetricRevision.ProcessorId.Value); writer.Write(cut.MetricRevision.StreamId.MachineId.Value.ToString("D"));
            writer.Write(cut.MetricRevision.StreamId.StreamKey); writer.Write(cut.MetricRevision.Position.Value);
            writer.Write(cut.QualityIdentity); writer.Write(cut.QualityContentFingerprint); writer.Write(assessment.Target.RuleVersion);
            writer.Write((int)assessment.Reason); Quantity(writer, assessment.Value); writer.Write(assessment.IsProvisional);
            writer.Write(assessment.Failures.Count);
            foreach (var failure in assessment.Failures)
            {
                writer.Write(failure.Dependency); Optional(writer, failure.MetricStatus.HasValue ? (int)failure.MetricStatus.Value : null);
                Optional(writer, failure.MetricReason.HasValue ? (int)failure.MetricReason.Value : null);
                writer.Write(failure.OperandName is not null); if (failure.OperandName is { } name) writer.Write(name);
                Optional(writer, failure.QualityReason.HasValue ? (int)failure.QualityReason.Value : null);
            }
        }
        if (stream.Length > MaximumLength) throw new InvalidDataException("Derived OEE payload too large.");
        return stream.ToArray();
    }
    internal static DerivedOeeAssessment Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0 || bytes.Length > MaximumLength) throw new InvalidDataException("Invalid derived OEE payload size.");
        try
        {
            using var stream = new MemoryStream(bytes, false); using var reader = new BinaryReader(stream, Utf8);
            if (reader.ReadString() != "FactoryConnect.DerivedOeePayload") throw new InvalidDataException("Invalid OEE header.");
            if (reader.ReadInt32() != Version) throw new NotSupportedException("Unsupported OEE storage encoding.");
            var identity = reader.ReadString(); var content = reader.ReadString();
            var quality = QualityStorageV1Codec.DecodeAssessment(Blob(reader));
            var availability = OperationalMetricDependencySnapshotV1Decoder.Decode(Blob(reader));
            var performance = OperationalMetricDependencySnapshotV1Decoder.Decode(Blob(reader));
            var decision = new QualityEvidenceReference(QualityEvidenceKind.SourceDependency, reader.ReadString(), reader.ReadString(), reader.ReadString());
            var state = (DerivedOeeCutState)reader.ReadInt32(); var processor = new OperationalMetricProjectionProcessorId(reader.ReadString());
            var aggregation = new MetricAggregationProcessorId(reader.ReadString());
            var machine = new MachineId(Guid.ParseExact(reader.ReadString(), "D")); var streamKey = reader.ReadString();
            var revision = new MetricAggregationCheckpoint(aggregation, new MetricInputStreamId(machine, streamKey), new MetricInputPosition(reader.ReadUInt64()));
            var cut = new DerivedOeeEvidenceCut(quality.Target.Scope, decision, state, processor, revision, reader.ReadString(), reader.ReadString());
            var target = new DerivedOeeTarget(cut, new(quality.Target.Scope, availability), new(quality.Target.Scope, performance), quality, reader.ReadString());
            var assessment = DerivedOeeAssessor.Assess(target);
            // Exact canonical re-encoding validates all stored output, failure reasons and provisional flag.
            if (identity != assessment.Identity || content != assessment.ContentFingerprint || !bytes.AsSpan().SequenceEqual(Encode(assessment)))
                throw new InvalidDataException("Derived OEE identity/content/result mismatch.");
            return assessment;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or FormatException or OverflowException)
        {
            throw new InvalidDataException("Corrupt derived OEE payload.", exception);
        }
    }
    private static void Blob(BinaryWriter writer, byte[] bytes) { writer.Write(bytes.Length); writer.Write(bytes); }
    private static byte[] Blob(BinaryReader reader)
    {
        var length = reader.ReadInt32(); if (length <= 0 || length > MaximumLength) throw new InvalidDataException("Invalid OEE blob length.");
        var bytes = reader.ReadBytes(length); if (bytes.Length != length) throw new EndOfStreamException(); return bytes;
    }
    private static void Quantity(BinaryWriter writer, decimal? value)
    {
        writer.Write(value.HasValue); if (value is { } number) writer.Write(number.ToString("G29", CultureInfo.InvariantCulture));
    }
    private static void Optional(BinaryWriter writer, int? value) { writer.Write(value.HasValue); if (value.HasValue) writer.Write(value.Value); }
}
