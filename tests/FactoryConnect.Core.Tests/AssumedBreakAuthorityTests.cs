using FactoryConnect.Abstractions;
using FactoryConnect.Core.AssumedBreakAuthority;
using Xunit;

namespace FactoryConnect.Core.Tests;

public sealed class AssumedBreakAuthorityTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 6, 0, 0, TimeSpan.Zero);
    private static AssumedBreakConfigurationSnapshot Snapshot() => new(new SiteId("GG"),
        new MachineId(Guid.Parse("677b030b-1ede-4a67-82ae-6a494791357f")), new DateOnly(2026, 10, 16),
        Start, Start.AddHours(8), "SHIFT-1", "schedule", "schedule-v1", "calendar-v1", "policy", "policy-v1",
        new DateOnly(2026, 10, 16), null, TimeSpan.FromMinutes(30), "demo instruction", ExclusionManifestState.CompleteEmpty, [], ["schedule-source", "calendar-source", "policy-source"]);
    private static AssumedBreakConfigurationAdmission Admission(AssumedBreakConfigurationSnapshot s) => new(s.Identity,
        s.SiteId, s.MachineId, s.ProductionDate, s.ProductionDate.AddDays(1), BreakAuthorityAdmissionMode.DemoProvisional,
        "demo-authority", "instruction-reference", ["admission-source"]);

    [Fact]
    public void SnapshotAndAdmissionRoundTripWithIndependentIdentities()
    {
        var s = Snapshot(); var a = Admission(s);
        foreach (var value in new AssumedBreakAuthorityRecord[] { s, a })
        {
            var bytes = AssumedBreakAuthorityV1Codec.Encode(value);
            var decoded = AssumedBreakAuthorityV1Codec.Decode(bytes, value.Identity);
            Assert.Equal(bytes, AssumedBreakAuthorityV1Codec.Encode(decoded));
            Assert.Equal(value.Identity, decoded.Identity);
        }
        Assert.NotEqual(s.Identity, a.Identity);
        Assert.NotEqual(a.Identity, (a with { AdmittingAuthority = "other" }).Identity);
        Assert.NotEqual(s.Identity, (s with { ScheduleVersion = "v2" }).Identity);
        Assert.NotEqual(s.Identity, (s with { CalendarVersion = "v2" }).Identity);
        Assert.NotEqual(s.Identity, (s with { PolicyVersion = "v2" }).Identity);
    }

    [Fact]
    public void MissingAndCompleteEmptyAreDistinctAndMissingCannotBeAdmitted()
    {
        var empty = Snapshot(); var missing = empty with { ExclusionState = ExclusionManifestState.Missing };
        Assert.NotEqual(empty.Identity, missing.Identity);
        Admission(empty).ValidateAgainst(empty);
        Assert.Throws<InvalidDataException>(() => Admission(missing).ValidateAgainst(missing));
        Assert.Throws<InvalidDataException>(() => missing.ExcludedDuration());
        Assert.Equal(TimeSpan.Zero, empty.ExcludedDuration());
    }

    [Fact]
    public void SourceOrderingAndDuplicatesDoNotChangeCanonicalIdentityOrUnion()
    {
        var a = new AuthorityExclusion("A", Start.AddHours(1), Start.AddMinutes(80), AuthorityExclusionClassification.OtherExclusion);
        var b = new AuthorityExclusion("B", Start.AddMinutes(70), Start.AddMinutes(90), AuthorityExclusionClassification.OtherExclusion);
        var first = Snapshot() with { ExclusionState = ExclusionManifestState.CompleteIntervals, Exclusions = [a, b] };
        var reordered = first with { Exclusions = [b, a, a], SourceReferences = ["policy-source", "calendar-source", "schedule-source", "policy-source"] };
        Assert.Equal(first.Identity, reordered.Identity);
        Assert.Equal(TimeSpan.FromMinutes(30), first.ExcludedDuration());
        Admission(first).ValidateAgainst(first);
    }

    [Fact]
    public void ConflictingDefinitionsAndDoubleBreakAuthorityAreRejected()
    {
        var a = new AuthorityExclusion("A", Start.AddHours(1), Start.AddMinutes(80), AuthorityExclusionClassification.OtherExclusion);
        var conflict = Snapshot() with { ExclusionState = ExclusionManifestState.CompleteIntervals, Exclusions = [a, a with { EndsAtUtc = Start.AddMinutes(90) }] };
        Assert.Throws<InvalidDataException>(() => conflict.Validate());
        var actual = Snapshot() with { ExclusionState = ExclusionManifestState.CompleteIntervals,
            Exclusions = [a with { Classification = AuthorityExclusionClassification.ActualBreak }] };
        actual.Validate();
        Assert.Throws<InvalidDataException>(() => Admission(actual).ValidateAgainst(actual));
    }

    [Fact]
    public void AdmissionScopeAndExclusiveEffectiveEndAreValidated()
    {
        var s = Snapshot(); var a = Admission(s);
        a.ValidateAgainst(s);
        Assert.Throws<InvalidDataException>(() => (a with { EffectiveFrom = s.ProductionDate.AddDays(1), EffectiveTo = null }).ValidateAgainst(s));
        Assert.Throws<InvalidDataException>(() => (a with { EffectiveFrom = s.ProductionDate.AddDays(-1), EffectiveTo = s.ProductionDate }).ValidateAgainst(s));
        Assert.Throws<InvalidDataException>(() => (a with { SiteId = new SiteId("OTHER") }).ValidateAgainst(s));
        Assert.Throws<InvalidDataException>(() => a.ValidateAgainst(s with { CalendarVersion = "new" }));
    }

    [Fact]
    public void MalformedUnsupportedAndIdentityMismatchAreExplicit()
    {
        var s = Snapshot(); var bytes = AssumedBreakAuthorityV1Codec.Encode(s);
        Assert.Throws<InvalidDataException>(() => AssumedBreakAuthorityV1Codec.Decode(bytes[..^1], s.Identity));
        Assert.Throws<InvalidDataException>(() => AssumedBreakAuthorityV1Codec.Decode([.. bytes, 0], s.Identity));
        Assert.Throws<InvalidDataException>(() => AssumedBreakAuthorityV1Codec.Decode(bytes, (s with { ScheduleVersion = "v2" }).Identity));
        bytes[0] = 2;
        Assert.Throws<NotSupportedException>(() => AssumedBreakAuthorityV1Codec.Decode(bytes, s.Identity));
    }

    [Fact]
    public void InvalidIntervalsAndMissingSourceReferencesAreRejected()
    {
        var s = Snapshot();
        Assert.Throws<InvalidDataException>(() => (s with { SourceReferences = [] }).Validate());
        Assert.Throws<InvalidDataException>(() => (s with { ShiftStartsAtUtc = Start.ToOffset(TimeSpan.FromHours(1)) }).Validate());
        Assert.Throws<InvalidDataException>(() => (s with { ExclusionState = ExclusionManifestState.CompleteIntervals,
            Exclusions = [new("outside", Start.AddMinutes(-1), Start.AddMinutes(2), AuthorityExclusionClassification.OtherExclusion)] }).Validate());
    }
}
