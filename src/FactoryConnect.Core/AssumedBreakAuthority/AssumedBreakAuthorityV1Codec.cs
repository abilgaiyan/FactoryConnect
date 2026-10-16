using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.AssumedBreakAuthority;

/// <summary>V1: little-endian integers, strict UTF-8 strings, UTC/duration ticks, day numbers,
/// sorted ordinal set references, sorted distinct interval evidence, explicit presence flags.</summary>
public static class AssumedBreakAuthorityV1Codec
{
    public const int MaximumBytes = 1048576;
    private const string SnapshotPrefix = "assumed-break-config:v1:";
    private const string AdmissionPrefix = "assumed-break-admission:v1:";

    public static string Identity(AssumedBreakAuthorityRecord value) =>
        (value is AssumedBreakConfigurationSnapshot ? SnapshotPrefix : AdmissionPrefix) +
        Convert.ToHexString(SHA256.HashData(Encode(value)));

    public static byte[] Key(string identity, bool snapshot)
    {
        var prefix = snapshot ? SnapshotPrefix : AdmissionPrefix;
        if (identity is null || !identity.StartsWith(prefix, StringComparison.Ordinal) || identity.Length != prefix.Length + 64)
            throw new InvalidDataException("Invalid authority identity.");
        try
        {
            var key = Convert.FromHexString(identity[prefix.Length..]);
            if (identity[prefix.Length..] != Convert.ToHexString(key))
                throw new InvalidDataException("Noncanonical authority identity.");
            return key;
        }
        catch (FormatException e) { throw new InvalidDataException("Invalid authority hash.", e); }
    }

    public static byte[] Encode(AssumedBreakAuthorityRecord value)
    {
        ArgumentNullException.ThrowIfNull(value); value.Validate();
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, new UTF8Encoding(false, true), true))
        {
            w.Write(1);
            switch (value)
            {
                case AssumedBreakConfigurationSnapshot s:
                    w.Write((byte)1); Scope(w, s.SiteId, s.MachineId);
                    w.Write(s.ProductionDate.DayNumber); w.Write(s.ShiftStartsAtUtc.UtcTicks); w.Write(s.ShiftEndsAtUtc.UtcTicks);
                    w.Write(s.ShiftId); w.Write(s.ScheduleId); w.Write(s.ScheduleVersion); w.Write(s.CalendarVersion);
                    w.Write(s.PolicyId); w.Write(s.PolicyVersion); w.Write(s.PolicyEffectiveFrom.DayNumber);
                    w.Write(s.PolicyEffectiveTo.HasValue); if (s.PolicyEffectiveTo is { } policyEnd) w.Write(policyEnd.DayNumber);
                    w.Write(s.BreakAllowance.Ticks); w.Write(s.InstructionReference);
                    w.Write((int)s.ExclusionState);
                    var intervals = s.Exclusions.Distinct().OrderBy(x => x.SourceReference, StringComparer.Ordinal)
                        .ThenBy(x => x.StartsAtUtc).ThenBy(x => x.EndsAtUtc).ThenBy(x => x.Classification).ToArray();
                    w.Write(intervals.Length);
                    foreach (var i in intervals)
                    { w.Write(i.SourceReference); w.Write(i.StartsAtUtc.UtcTicks); w.Write(i.EndsAtUtc.UtcTicks); w.Write((int)i.Classification); }
                    References(w, s.SourceReferences);
                    break;
                case AssumedBreakConfigurationAdmission a:
                    w.Write((byte)2); w.Write(a.SnapshotIdentity); Scope(w, a.SiteId, a.MachineId);
                    w.Write(a.EffectiveFrom.DayNumber); w.Write(a.EffectiveTo.HasValue);
                    if (a.EffectiveTo is { } end) w.Write(end.DayNumber);
                    w.Write((int)a.Mode); w.Write(a.AdmittingAuthority); w.Write(a.InstructionReference); References(w, a.SourceReferences);
                    break;
                default: throw new NotSupportedException("Unsupported authority record.");
            }
        }
        if (stream.Length > MaximumBytes) throw new InvalidDataException("Authority payload exceeds V1 limit.");
        return stream.ToArray();
    }

    public static AssumedBreakAuthorityRecord Decode(byte[] payload, string expectedIdentity)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length == 0 || payload.Length > MaximumBytes) throw new InvalidDataException("Invalid authority payload length.");
        try
        {
            using var stream = new MemoryStream(payload, false);
            using var r = new BinaryReader(stream, new UTF8Encoding(false, true));
            if (r.ReadInt32() != 1) throw new NotSupportedException("Unsupported authority encoding.");
            var kind = r.ReadByte();
            AssumedBreakAuthorityRecord value;
            if (kind == 1)
            {
                var site = new SiteId(r.ReadString()); var machine = new MachineId(ReadGuid(r));
                var day = DateOnly.FromDayNumber(r.ReadInt32());
                var start = new DateTimeOffset(r.ReadInt64(), TimeSpan.Zero); var end = new DateTimeOffset(r.ReadInt64(), TimeSpan.Zero);
                var shift = r.ReadString(); var schedule = r.ReadString(); var version = r.ReadString(); var calendar = r.ReadString();
                var policy = r.ReadString(); var policyVersion = r.ReadString();
                var policyFrom = DateOnly.FromDayNumber(r.ReadInt32());
                DateOnly? policyTo = r.ReadBoolean() ? DateOnly.FromDayNumber(r.ReadInt32()) : null;
                var allowance = TimeSpan.FromTicks(r.ReadInt64()); var instruction = r.ReadString();
                var state = (ExclusionManifestState)r.ReadInt32();
                var count = Count(r); var intervals = ImmutableArray.CreateBuilder<AuthorityExclusion>(count);
                for (var i = 0; i < count; i++) intervals.Add(new(r.ReadString(),
                    new(r.ReadInt64(), TimeSpan.Zero), new(r.ReadInt64(), TimeSpan.Zero), (AuthorityExclusionClassification)r.ReadInt32()));
                value = new AssumedBreakConfigurationSnapshot(site, machine, day, start, end, shift, schedule, version, calendar,
                    policy, policyVersion, policyFrom, policyTo, allowance, instruction, state, intervals.ToImmutable(), References(r));
            }
            else if (kind == 2)
            {
                var snapshot = r.ReadString(); var site = new SiteId(r.ReadString()); var machine = new MachineId(ReadGuid(r));
                var from = DateOnly.FromDayNumber(r.ReadInt32()); DateOnly? to = r.ReadBoolean() ? DateOnly.FromDayNumber(r.ReadInt32()) : null;
                value = new AssumedBreakConfigurationAdmission(snapshot, site, machine, from, to,
                    (BreakAuthorityAdmissionMode)r.ReadInt32(), r.ReadString(), r.ReadString(), References(r));
            }
            else throw new NotSupportedException("Unsupported authority record kind.");
            value.Validate();
            if (stream.Position != stream.Length || value.Identity != expectedIdentity || !Encode(value).AsSpan().SequenceEqual(payload))
                throw new InvalidDataException("Authority identity/content/canonical encoding mismatch.");
            return value;
        }
        catch (IOException e) { throw new InvalidDataException("Malformed authority payload.", e); }
        catch (ArgumentException e) { throw new InvalidDataException("Malformed authority payload.", e); }
    }

    private static void Scope(BinaryWriter w, SiteId site, MachineId machine) { w.Write(site.Value); w.Write(machine.Value.ToByteArray()); }
    private static void References(BinaryWriter w, ImmutableArray<string> references)
    {
        var sorted = references.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        w.Write(sorted.Length); foreach (var item in sorted) w.Write(item);
    }
    private static ImmutableArray<string> References(BinaryReader r)
    {
        var count = Count(r); var values = ImmutableArray.CreateBuilder<string>(count);
        for (var i = 0; i < count; i++) values.Add(r.ReadString());
        return values.ToImmutable();
    }
    private static int Count(BinaryReader r)
    {
        var count = r.ReadInt32();
        if (count < 0 || count > 16384) throw new InvalidDataException("Invalid authority collection size.");
        return count;
    }
    private static Guid ReadGuid(BinaryReader r)
    {
        var bytes = r.ReadBytes(16);
        if (bytes.Length != 16) throw new InvalidDataException("Truncated GUID.");
        return new(bytes);
    }
}
