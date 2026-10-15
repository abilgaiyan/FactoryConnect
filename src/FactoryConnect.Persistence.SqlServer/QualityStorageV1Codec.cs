using System.Globalization;
using System.Text;
using FactoryConnect.Abstractions;
using FactoryConnect.Core.Quality;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>Independent, bounded Quality storage codec. Never changes operational metric codec V1.</summary>
internal static class QualityStorageV1Codec
{
    internal const int Version = 1;
    internal const int MaximumPayloadLength = 16_777_216;
    private const int MaximumItems = 10_000;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static byte[] PolicyIdentity(QualityPolicyVersion identity) => Write(writer =>
    {
        Header(writer, "FactoryConnect.QualityPolicyIdentity");
        writer.Write(identity.PolicyId); writer.Write(identity.Version);
    });

    internal static byte[] EncodePolicy(ApprovedAllGoodQualityPolicy policy) => Write(writer =>
    {
        Header(writer, "FactoryConnect.QualityPolicyPayload");
        WritePolicy(writer, policy);
    });

    internal static ApprovedAllGoodQualityPolicy DecodePolicy(byte[] payload) => Decode(payload, reader =>
    {
        Header(reader, "FactoryConnect.QualityPolicyPayload");
        return ReadPolicy(reader);
    }, EncodePolicy);

    internal static byte[] EncodeAssessment(QualityAssessment assessment) => Write(writer =>
    {
        Header(writer, "FactoryConnect.QualityAssessmentPayload");
        writer.Write(assessment.Identity); writer.Write(assessment.ContentFingerprint);
        var target = QualityAssessmentIdentity.Encode(assessment.Target, true);
        writer.Write(target.Length); writer.Write(target);
        writer.Write((int)assessment.Basis); writer.Write((int)assessment.Reason);
        Quantity(writer, assessment.Value); Quantity(writer, assessment.GoodQuantity); Quantity(writer, assessment.RejectedQuantity);
        writer.Write(assessment.AppliedPolicy is not null);
        if (assessment.AppliedPolicy is { } policy) { writer.Write(policy.PolicyId); writer.Write(policy.Version); }
    });

    internal static QualityAssessment DecodeAssessment(byte[] payload) => Decode(payload, reader =>
    {
        Header(reader, "FactoryConnect.QualityAssessmentPayload");
        var identity = reader.ReadString(); var fingerprint = reader.ReadString();
        var length = reader.ReadInt32();
        if (length <= 0 || length > MaximumPayloadLength) throw new InvalidDataException("Invalid target length.");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        var target = DecodeTarget(bytes);
        var assessment = QualityAssessor.Assess(target);
        if (!string.Equals(identity, assessment.Identity, StringComparison.Ordinal) ||
            !string.Equals(fingerprint, assessment.ContentFingerprint, StringComparison.Ordinal) ||
            reader.ReadInt32() != (int)assessment.Basis || reader.ReadInt32() != (int)assessment.Reason ||
            Quantity(reader) != assessment.Value || Quantity(reader) != assessment.GoodQuantity || Quantity(reader) != assessment.RejectedQuantity)
            throw new InvalidDataException("Quality result does not match its decoded evidence and rule.");
        var applied = reader.ReadBoolean() ? new QualityPolicyVersion(reader.ReadString(), reader.ReadString()) : null;
        if (applied != assessment.AppliedPolicy) throw new InvalidDataException("Applied policy differs from the decoded result.");
        return assessment;
    }, EncodeAssessment);

    private static QualityAssessmentTarget DecodeTarget(byte[] payload) => Decode(payload, reader =>
    {
        Header(reader, "FactoryConnect.QualityAssessment");
        if (!reader.ReadBoolean()) throw new InvalidDataException("Expected full target content.");
        var site = new SiteId(reader.ReadString());
        var machine = new MachineId(Guid.ParseExact(reader.ReadString(), "D"));
        var periodTag = reader.ReadByte();
        string? schedule = null; string? shift = null; DateOnly? day = null;
        if (periodTag == 1) { schedule = reader.ReadString(); shift = reader.ReadString(); }
        else if (periodTag == 2) day = DateOnly.FromDayNumber(reader.ReadInt32());
        else throw new InvalidDataException("Unknown Quality period tag.");
        var start = Utc(reader); var end = Utc(reader);
        var order = Optional(reader); var operation = Optional(reader); var part = Optional(reader); var person = Optional(reader);
        var context = new OperationalMetricEvaluationContextKey
        {
            ProductionOrderId = order is null ? null : new ProductionOrderId(order),
            OperationId = operation is null ? null : new OperationId(operation),
            PartId = part is null ? null : new PartId(part),
            OperatorId = person is null ? null : new OperatorId(person),
        };
        OperationalMetricPeriodId period = periodTag == 1
            ? new OperationalMetricPeriodId.Shift(new ShiftOccurrenceId(site, new ShiftScheduleAssignmentId(schedule!), new ShiftId(shift!), start, end))
            : new OperationalMetricPeriodId.ProductionDay(new ProductionDayId(site, day!.Value));
        var scope = new QualityAssessmentScope(machine, period, start, end, context);
        var rule = reader.ReadString();
        var admissionDecision = Reference(reader) ?? throw new InvalidDataException("Admission decision missing.");
        var admissionRule = reader.ReadString(); var population = Reference(reader); var dependencies = References(reader);
        var measurementDecision = Reference(reader) ?? throw new InvalidDataException("Measurement decision missing.");
        var measuredPopulation = Reference(reader); var evidence = References(reader);
        var policies = new ApprovedAllGoodQualityPolicy[Count(reader)];
        for (var index = 0; index < policies.Length; index++) policies[index] = ReadPolicy(reader);
        var productionState = (QualityProductionAdmissionState)reader.ReadInt32(); var produced = Quantity(reader);
        var measurementState = (QualityMeasurementState)reader.ReadInt32();
        var measuredProduced = Quantity(reader); var good = Quantity(reader); var rejected = Quantity(reader);
        return new QualityAssessmentTarget(scope,
            new QualityProductionAdmission(scope, productionState, admissionDecision, admissionRule, population, produced, dependencies),
            new QualityMeasurementCoverage(scope, measurementState, measurementDecision, measuredPopulation, measuredProduced, good, rejected, evidence),
            policies, rule);
    }, static target => QualityAssessmentIdentity.Encode(target, true));

    private static void WritePolicy(BinaryWriter writer, ApprovedAllGoodQualityPolicy policy)
    {
        writer.Write(policy.Identity.PolicyId); writer.Write(policy.Identity.Version); writer.Write(policy.SiteId.Value);
        var machines = policy.Machines.Select(static machine => machine.Value.ToString("D")).Order(StringComparer.Ordinal).ToArray();
        writer.Write(machines.Length); foreach (var machine in machines) writer.Write(machine);
        writer.Write(policy.ApprovalReference); writer.Write(policy.ApprovedBy); writer.Write(policy.EffectiveFromUtc.Ticks);
        writer.Write(policy.EffectiveToUtc.HasValue); if (policy.EffectiveToUtc is { } until) writer.Write(until.Ticks);
    }
    private static ApprovedAllGoodQualityPolicy ReadPolicy(BinaryReader reader)
    {
        var identity = new QualityPolicyVersion(reader.ReadString(), reader.ReadString()); var site = new SiteId(reader.ReadString());
        var machines = new MachineId[Count(reader)];
        for (var index = 0; index < machines.Length; index++) machines[index] = new MachineId(Guid.ParseExact(reader.ReadString(), "D"));
        var approval = reader.ReadString(); var approver = reader.ReadString(); var from = Utc(reader);
        DateTimeOffset? until = reader.ReadBoolean() ? Utc(reader) : null;
        return new ApprovedAllGoodQualityPolicy(identity, site, machines, approval, approver, from, until);
    }
    private static QualityEvidenceReference? Reference(BinaryReader reader) => reader.ReadBoolean()
        ? new QualityEvidenceReference((QualityEvidenceKind)reader.ReadInt32(), reader.ReadString(), reader.ReadString(), reader.ReadString()) : null;
    private static QualityEvidenceReference[] References(BinaryReader reader)
    {
        var items = new QualityEvidenceReference[Count(reader)];
        for (var index = 0; index < items.Length; index++) items[index] = Reference(reader) ?? throw new InvalidDataException("Null evidence entry.");
        return items;
    }
    private static int Count(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > MaximumItems) throw new InvalidDataException("Invalid collection length.");
        return count;
    }
    private static DateTimeOffset Utc(BinaryReader reader) => new(reader.ReadInt64(), TimeSpan.Zero);
    private static string? Optional(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadString() : null;
    private static decimal? Quantity(BinaryReader reader) => reader.ReadBoolean()
        ? decimal.Parse(reader.ReadString(), NumberStyles.Float, CultureInfo.InvariantCulture) : null;
    private static void Quantity(BinaryWriter writer, decimal? value)
    {
        writer.Write(value.HasValue); if (value is { } number) writer.Write(number.ToString("G29", CultureInfo.InvariantCulture));
    }
    private static void Header(BinaryWriter writer, string magic) { writer.Write(magic); writer.Write(Version); }
    private static void Header(BinaryReader reader, string magic)
    {
        if (!string.Equals(reader.ReadString(), magic, StringComparison.Ordinal)) throw new InvalidDataException("Invalid Quality payload header.");
        if (reader.ReadInt32() != Version) throw new NotSupportedException("Unsupported Quality payload version.");
    }
    private static byte[] Write(Action<BinaryWriter> encode)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Utf8, true)) encode(writer);
        if (stream.Length > MaximumPayloadLength) throw new InvalidDataException("Quality payload exceeds its size bound.");
        return stream.ToArray();
    }
    private static T Decode<T>(byte[] bytes, Func<BinaryReader, T> decode, Func<T, byte[]> encode)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0 || bytes.Length > MaximumPayloadLength) throw new InvalidDataException("Invalid Quality payload size.");
        try
        {
            using var stream = new MemoryStream(bytes, false);
            using var reader = new BinaryReader(stream, Utf8);
            var value = decode(reader);
            if (stream.Position != stream.Length || !bytes.AsSpan().SequenceEqual(encode(value)))
                throw new InvalidDataException("Quality payload is not a complete canonical encoding.");
            return value;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or FormatException or OverflowException)
        {
            throw new InvalidDataException("Invalid Quality payload.", exception);
        }
    }
}
