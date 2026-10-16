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
