using System.Security.Cryptography;
using System.Text;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Core;

/// <summary>
/// V1 canonical binary encoding: ordinal UTF-8 strings via BinaryWriter, little-endian
/// integers, UTC ticks (100 ns), duration ticks (100 ns), DateOnly day numbers,
/// Boolean presence flags for nullable values, and fixed field order.
/// Exclusion fingerprint is produced upstream using ordinal ID, UTC bounds, IsBreak.
/// </summary>
public static class AssumedBreakAssessmentV1Codec
{
    private const int Version = 1;
    private const int MaximumBytes = 1048576;
    private const string Prefix = "assumed-break-assessment:v1:";

    public static string Identity(AssumedBreakAssessmentTarget target) =>
        Prefix + Convert.ToHexString(SHA256.HashData(EncodeTarget(target)));

    public static byte[] EncodeTarget(AssumedBreakAssessmentTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.Validate();
        return Write(w => WriteTarget(w, target));
    }

    public static byte[] Encode(DurableAssumedBreakAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        assessment.Validate();
        return Write(w =>
        {
            w.Write(Version);
            WriteTarget(w, assessment.Target);
            w.Write(assessment.PlannedTime is not null);
            if (assessment.PlannedTime is { } p)
            {
                w.Write(p.EligibleScheduledDuration.Ticks);
                w.Write(p.AssumedBreakDeduction.Ticks);
                w.Write(p.PlannedProductionDuration.Ticks);
                w.Write(p.IsForecast);
                w.Write(p.IsPolicyAssumed);
            }
            w.Write(assessment.ObservedRunningDuration.HasValue);
            if (assessment.ObservedRunningDuration is { } running) w.Write(running.Ticks);
            w.Write(assessment.AvailabilityReason);
            w.Write(assessment.AvailabilityPercentage.HasValue);
            if (assessment.AvailabilityPercentage is { } value) w.Write(value);
        });
    }

    public static DurableAssumedBreakAssessment Decode(byte[] payload, string? expectedIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length == 0 || payload.Length > MaximumBytes)
            throw new InvalidDataException("Invalid payload length.");
        try
        {
            using var stream = new MemoryStream(payload, false);
            using var r = new BinaryReader(stream, new UTF8Encoding(false, true));
            if (r.ReadInt32() != Version) throw new NotSupportedException("Unsupported assessment version.");
            var target = ReadTarget(r);
            AssumedShiftBreakResult? planned = null;
            if (r.ReadBoolean())
                planned = new(TimeSpan.FromTicks(r.ReadInt64()), TimeSpan.FromTicks(r.ReadInt64()),
                    TimeSpan.FromTicks(r.ReadInt64()), r.ReadBoolean(), r.ReadBoolean());
            TimeSpan? running = r.ReadBoolean() ? TimeSpan.FromTicks(r.ReadInt64()) : null;
            var reason = r.ReadString();
            decimal? percentage = r.ReadBoolean() ? r.ReadDecimal() : null;
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing bytes.");
            var assessment = new DurableAssumedBreakAssessment(target, planned, running, reason, percentage);
            assessment.Validate();
            if (expectedIdentity is not null &&
                !string.Equals(assessment.Identity, expectedIdentity, StringComparison.Ordinal))
                throw new InvalidDataException("Exact identity mismatch.");
            if (!Encode(assessment).AsSpan().SequenceEqual(payload))
                throw new InvalidDataException("Noncanonical payload.");
            return assessment;
        }
        catch (EndOfStreamException e) { throw new InvalidDataException("Truncated payload.", e); }
        catch (IOException e) { throw new InvalidDataException("Malformed payload.", e); }
        catch (ArgumentException e) { throw new InvalidDataException("Malformed payload.", e); }
    }

    private static byte[] Write(Action<BinaryWriter> action)
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, new UTF8Encoding(false, true), true)) action(w);
        if (stream.Length > MaximumBytes) throw new InvalidDataException("V1 payload too large.");
        return stream.ToArray();
    }

    private static void WriteTarget(BinaryWriter w, AssumedBreakAssessmentTarget t)
    {
        w.Write(Version);
        w.Write(t.SiteId.Value);
        w.Write(t.MachineId.Value.ToByteArray());
        w.Write(t.ProductionDate.DayNumber);
        w.Write(t.ShiftStartUtc.UtcTicks);
        w.Write(t.ShiftEndUtc.UtcTicks);
        w.Write(t.ScheduleId);
        w.Write(t.CalendarVersion);
        w.Write(t.CalculationRuleVersion);
        w.Write(t.AdmissionMode);
        w.Write(t.PolicyId);
        w.Write(t.PolicyVersion);
        w.Write(t.PolicyEffectiveFrom.DayNumber);
        w.Write(t.PolicyEffectiveTo.HasValue);
        if (t.PolicyEffectiveTo is { } end) w.Write(end.DayNumber);
        w.Write(t.PolicyAllowance.Ticks);
        w.Write(t.PolicyInstructionReference);
        w.Write(t.AggregationRevision.ProcessorId.Value);
        w.Write(t.AggregationRevision.StreamId.MachineId.Value.ToByteArray());
        w.Write(t.AggregationRevision.StreamId.StreamKey);
        w.Write(t.AggregationRevision.Position.Value);
        w.Write(t.RunningEvidenceReference is not null);
        if (t.RunningEvidenceReference is not null) w.Write(t.RunningEvidenceReference);
        w.Write(t.ExclusionEvidenceFingerprint);
    }

    private static AssumedBreakAssessmentTarget ReadTarget(BinaryReader r)
    {
        if (r.ReadInt32() != Version) throw new NotSupportedException("Unsupported target version.");
        var site = new SiteId(r.ReadString());
        var machine = new MachineId(ReadGuid(r));
        var date = DateOnly.FromDayNumber(r.ReadInt32());
        var start = new DateTimeOffset(r.ReadInt64(), TimeSpan.Zero);
        var end = new DateTimeOffset(r.ReadInt64(), TimeSpan.Zero);
        var schedule = r.ReadString();
        var calendar = r.ReadString();
        var rule = r.ReadString();
        var admission = r.ReadString();
        var policy = r.ReadString();
        var policyVersion = r.ReadString();
        var effectiveFrom = DateOnly.FromDayNumber(r.ReadInt32());
        DateOnly? effectiveTo = r.ReadBoolean() ? DateOnly.FromDayNumber(r.ReadInt32()) : null;
        var allowance = TimeSpan.FromTicks(r.ReadInt64());
        var instruction = r.ReadString();
        var processor = new MetricAggregationProcessorId(r.ReadString());
        var streamMachine = new MachineId(ReadGuid(r));
        var streamKey = r.ReadString();
        var position = new MetricInputPosition(r.ReadUInt64());
        var running = r.ReadBoolean() ? r.ReadString() : null;
        var exclusions = r.ReadString();
        return new(site, machine, date, start, end, schedule, calendar, rule, admission,
            policy, policyVersion, effectiveFrom, effectiveTo, allowance, instruction,
            new(processor, new(streamMachine, streamKey), position), running, exclusions);
    }

    private static Guid ReadGuid(BinaryReader r)
    {
        var bytes = r.ReadBytes(16);
        if (bytes.Length != 16) throw new InvalidDataException("Truncated GUID.");
        return new Guid(bytes);
    }
}
