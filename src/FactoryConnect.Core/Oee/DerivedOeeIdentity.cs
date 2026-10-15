using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FactoryConnect.Abstractions;
using FactoryConnect.Core.Quality;

namespace FactoryConnect.Core.Oee;

/// <summary>Independent DOAI V1 canonical encoding; immutable reference identity is separate from payload integrity.</summary>
public static class DerivedOeeIdentity
{
    public static string Create(DerivedOeeTarget target) => "derived-oee:v1:" + Convert.ToHexString(SHA256.HashData(Encode(target, false)));
    public static string ContentFingerprint(DerivedOeeTarget target) => Convert.ToHexString(SHA256.HashData(Encode(target, true)));
    public static byte[] Encode(DerivedOeeTarget target, bool includeContent)
    {
        ArgumentNullException.ThrowIfNull(target);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true);
        writer.Write("FactoryConnect.DerivedOeeIdentity"); writer.Write(1); writer.Write(includeContent);
        writer.Write(target.RuleVersion);
        // QAI includes the typed scope, exact interval/context and all Quality evidence references.
        var quality = QualityAssessmentIdentity.Encode(target.Quality.Target, includeContent);
        writer.Write(quality.Length); writer.Write(quality); writer.Write(target.Quality.Identity);
        var cut = target.Cut;
        writer.Write((int)cut.Decision.Kind); writer.Write(cut.Decision.Authority); writer.Write(cut.Decision.Identity); writer.Write(cut.Decision.Revision);
        writer.Write(cut.ProjectionProcessor.Value); Revision(writer, cut.MetricRevision);
        if (includeContent)
        {
            writer.Write((int)cut.State); writer.Write(cut.QualityContentFingerprint);
            writer.Write(target.Quality.ContentFingerprint);
            Quantity(writer, target.Quality.Value); writer.Write((int)target.Quality.Basis); writer.Write((int)target.Quality.Reason);
        }
        Projection(writer, target.Availability.Projection, includeContent);
        Projection(writer, target.Performance.Projection, includeContent);
        writer.Flush(); return stream.ToArray();
    }
    private static void Projection(BinaryWriter writer, OperationalMetricProjection projection, bool content)
    {
        writer.Write(projection.ProcessorId.Value); writer.Write(projection.Key.DefinitionId.MetricKey); writer.Write(projection.Key.DefinitionId.Version);
        Revision(writer, projection.SourceRevision);
        if (!content) return;
        writer.Write((int)projection.Status); Quantity(writer, projection.Value); writer.Write(projection.Unit);
        writer.Write(projection.ReasonCode.HasValue); if (projection.ReasonCode is { } reason) writer.Write((int)reason);
        writer.Write(projection.ReasonOperandName is not null); if (projection.ReasonOperandName is { } operand) writer.Write(operand);
        writer.Write(projection.OperandEvidence.Count);
        foreach (var evidence in projection.OperandEvidence.OrderBy(static item => item.OperandName, StringComparer.Ordinal))
        {
            writer.Write(evidence.OperandName); writer.Write(evidence.SourceIdentity.ProcessorId.Value);
            writer.Write(evidence.SourceIdentity.MachineId.Value.ToString("D")); writer.Write(evidence.SourceIdentity.ComponentKey);
            Revision(writer, evidence.SourceRevision); writer.Write((int)evidence.Dimension); Quantity(writer, evidence.Value);
            writer.Write(evidence.Unit); writer.Write(evidence.InputCount);
            writer.Write(evidence.FirstInputTimestamp.UtcTicks); writer.Write(evidence.LastInputTimestamp.UtcTicks);
        }
    }
    private static void Revision(BinaryWriter writer, MetricAggregationCheckpoint revision)
    {
        writer.Write(revision.ProcessorId.Value); writer.Write(revision.StreamId.MachineId.Value.ToString("D"));
        writer.Write(revision.StreamId.StreamKey); writer.Write(revision.Position.Value);
    }
    private static void Quantity(BinaryWriter writer, decimal? value)
    {
        writer.Write(value.HasValue); if (value is { } number) writer.Write(number.ToString("G29", CultureInfo.InvariantCulture));
    }
}
