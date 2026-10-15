using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Quality;

/// <summary>
/// QAI V1: strict UTF-8 BinaryWriter strings (7-bit byte length), little-endian integers,
/// explicit nullable markers, UTC ticks, tagged period identities and ordinal sorted sets.
/// Identity contains exact references; content fingerprint additionally binds their immutable payloads.
/// Independent of the frozen operational-metric V1 codecs.
/// </summary>
public static class QualityAssessmentIdentity
{
    public const int EncodingVersion = 1;
    public static string Create(QualityAssessmentTarget target) => "quality-assessment:v1:" + Hash(Encode(target, false));
    public static string ContentFingerprint(QualityAssessmentTarget target) => Hash(Encode(target, true));

    public static byte[] Encode(QualityAssessmentTarget target, bool includeContent)
    {
        ArgumentNullException.ThrowIfNull(target);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), leaveOpen: true);
        writer.Write("FactoryConnect.QualityAssessment");
        writer.Write(EncodingVersion);
        writer.Write(includeContent);
        WriteScope(writer, target.Scope);
        writer.Write(target.RuleVersion);
        var production = target.Production;
        WriteReference(writer, production.Decision);
        writer.Write(production.RuleVersion);
        WriteReference(writer, production.Population);
        WriteReferences(writer, production.Dependencies);
        var measurement = target.Measurement;
        WriteReference(writer, measurement.Decision);
        WriteReference(writer, measurement.Population);
        WriteReferences(writer, measurement.Evidence);
        var policies = target.Policies.OrderBy(static policy => policy.Identity.PolicyId, StringComparer.Ordinal)
            .ThenBy(static policy => policy.Identity.Version, StringComparer.Ordinal).ToArray();
        writer.Write(policies.Length); // zero is explicit policy absence
        foreach (var policy in policies)
        {
            writer.Write(policy.Identity.PolicyId);
            writer.Write(policy.Identity.Version);
            if (includeContent)
            {
                writer.Write(policy.SiteId.Value);
                var machines = policy.Machines.Select(static machine => machine.Value.ToString("D"))
                    .Order(StringComparer.Ordinal).ToArray();
                writer.Write(machines.Length);
                foreach (var machine in machines) writer.Write(machine);
                writer.Write(policy.ApprovalReference);
                writer.Write(policy.ApprovedBy);
                writer.Write(policy.EffectiveFromUtc.Ticks);
                writer.Write(policy.EffectiveToUtc.HasValue);
                if (policy.EffectiveToUtc is { } until) writer.Write(until.Ticks);
            }
        }
        if (includeContent)
        {
            writer.Write((int)production.State);
            WriteQuantity(writer, production.ProducedQuantity);
            writer.Write((int)measurement.State);
            WriteQuantity(writer, measurement.ProducedQuantity);
            WriteQuantity(writer, measurement.GoodQuantity);
            WriteQuantity(writer, measurement.RejectedQuantity);
        }
        writer.Flush();
        return stream.ToArray();
    }

    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));

    private static void WriteQuantity(BinaryWriter writer, decimal? value)
    {
        writer.Write(value.HasValue);
        if (value is { } quantity) writer.Write(quantity.ToString("G29", CultureInfo.InvariantCulture));
    }

    private static void WriteScope(BinaryWriter writer, QualityAssessmentScope scope)
    {
        writer.Write(scope.SiteId.Value);
        writer.Write(scope.MachineId.Value.ToString("D"));
        switch (scope.PeriodId)
        {
            case OperationalMetricPeriodId.Shift shift:
                writer.Write((byte)1);
                writer.Write(shift.ShiftOccurrenceId.ShiftScheduleAssignmentId.Value);
                writer.Write(shift.ShiftOccurrenceId.ShiftId.Value);
                break;
            case OperationalMetricPeriodId.ProductionDay day:
                writer.Write((byte)2);
                writer.Write(day.ProductionDayId.BusinessDate.DayNumber);
                break;
            default:
                throw new InvalidDataException("Unsupported Quality period.");
        }
        writer.Write(scope.StartsAtUtc.Ticks);
        writer.Write(scope.EndsAtUtc.Ticks);
        WriteOptional(writer, scope.Context.ProductionOrderId?.Value);
        WriteOptional(writer, scope.Context.OperationId?.Value);
        WriteOptional(writer, scope.Context.PartId?.Value);
        WriteOptional(writer, scope.Context.OperatorId?.Value);
    }

    private static void WriteOptional(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null) writer.Write(value);
    }

    private static void WriteReference(BinaryWriter writer, QualityEvidenceReference? reference)
    {
        writer.Write(reference is not null);
        if (reference is null) return;
        writer.Write((int)reference.Kind);
        writer.Write(reference.Authority);
        writer.Write(reference.Identity);
        writer.Write(reference.Revision);
    }

    private static void WriteReferences(BinaryWriter writer, IReadOnlyList<QualityEvidenceReference> references)
    {
        writer.Write(references.Count);
        foreach (var reference in references.OrderBy(static item => item.Kind)
            .ThenBy(static item => item.Authority, StringComparer.Ordinal)
            .ThenBy(static item => item.Identity, StringComparer.Ordinal)
            .ThenBy(static item => item.Revision, StringComparer.Ordinal)) WriteReference(writer, reference);
    }
}
