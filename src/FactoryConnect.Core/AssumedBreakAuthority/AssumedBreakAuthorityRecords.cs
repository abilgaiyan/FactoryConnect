using System.Collections.Immutable;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.AssumedBreakAuthority;

public enum ExclusionManifestState { Missing, CompleteEmpty, CompleteIntervals }
public enum BreakAuthorityAdmissionMode { DemoProvisional, FormallyApproved }
public enum AuthorityExclusionClassification { OtherExclusion, ActualBreak }

public sealed record AuthorityExclusion(string SourceReference, DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc, AuthorityExclusionClassification Classification);

public abstract record AssumedBreakAuthorityRecord
{
    public string Identity => AssumedBreakAuthorityV1Codec.Identity(this);
    public abstract void Validate();
}

/// <summary>Immutable configuration evidence. Persistence alone does not admit this snapshot.</summary>
public sealed record AssumedBreakConfigurationSnapshot(
    SiteId SiteId, MachineId MachineId, DateOnly ProductionDate,
    DateTimeOffset ShiftStartsAtUtc, DateTimeOffset ShiftEndsAtUtc,
    string ShiftId, string ScheduleId, string ScheduleVersion, string CalendarVersion,
    string PolicyId, string PolicyVersion, DateOnly PolicyEffectiveFrom, DateOnly? PolicyEffectiveTo,
    TimeSpan BreakAllowance, string InstructionReference,
    ExclusionManifestState ExclusionState, ImmutableArray<AuthorityExclusion> Exclusions,
    ImmutableArray<string> SourceReferences) : AssumedBreakAuthorityRecord
{
    public override void Validate()
    {
        if (SiteId.IsEmpty || MachineId.IsEmpty || ShiftStartsAtUtc.Offset != TimeSpan.Zero ||
            ShiftEndsAtUtc.Offset != TimeSpan.Zero || ShiftStartsAtUtc >= ShiftEndsAtUtc ||
            string.IsNullOrWhiteSpace(ShiftId) || string.IsNullOrWhiteSpace(ScheduleId) ||
            string.IsNullOrWhiteSpace(ScheduleVersion) || string.IsNullOrWhiteSpace(CalendarVersion) ||
            string.IsNullOrWhiteSpace(PolicyId) || string.IsNullOrWhiteSpace(PolicyVersion) ||
            PolicyEffectiveFrom > ProductionDate || (PolicyEffectiveTo is { } policyEnd &&
                (policyEnd <= PolicyEffectiveFrom || ProductionDate >= policyEnd)) ||
            BreakAllowance != TimeSpan.FromMinutes(30) || string.IsNullOrWhiteSpace(InstructionReference) ||
            !Enum.IsDefined(ExclusionState) || Exclusions.IsDefault || Exclusions.Length > 16384 || SourceReferences.IsDefaultOrEmpty || SourceReferences.Length > 16384 ||
            SourceReferences.Any(string.IsNullOrWhiteSpace) ||
            (ExclusionState != ExclusionManifestState.CompleteIntervals && Exclusions.Length != 0) ||
            (ExclusionState == ExclusionManifestState.CompleteIntervals && Exclusions.Length == 0))
            throw new InvalidDataException("Invalid assumed-break configuration snapshot.");
        var definitions = new Dictionary<string, AuthorityExclusion>(StringComparer.Ordinal);
        foreach (var interval in Exclusions)
        {
            if (interval is null || string.IsNullOrWhiteSpace(interval.SourceReference) ||
                interval.StartsAtUtc.Offset != TimeSpan.Zero || interval.EndsAtUtc.Offset != TimeSpan.Zero ||
                interval.StartsAtUtc >= interval.EndsAtUtc || interval.StartsAtUtc < ShiftStartsAtUtc ||
                interval.EndsAtUtc > ShiftEndsAtUtc || !Enum.IsDefined(interval.Classification) ||
                (definitions.TryGetValue(interval.SourceReference, out var previous) && previous != interval))
                throw new InvalidDataException("Invalid or conflicting exclusion evidence.");
            definitions[interval.SourceReference] = interval;
        }
    }

    public TimeSpan ExcludedDuration()
    {
        Validate();
        if (ExclusionState == ExclusionManifestState.Missing)
            throw new InvalidDataException("Exclusion authority is not established.");
        var total = TimeSpan.Zero;
        DateTimeOffset? start = null;
        var end = default(DateTimeOffset);
        foreach (var interval in Exclusions.Distinct().OrderBy(x => x.StartsAtUtc).ThenBy(x => x.EndsAtUtc))
        {
            if (start is null) { start = interval.StartsAtUtc; end = interval.EndsAtUtc; }
            else if (interval.StartsAtUtc <= end) { if (interval.EndsAtUtc > end) end = interval.EndsAtUtc; }
            else { total += end - start.Value; start = interval.StartsAtUtc; end = interval.EndsAtUtc; }
        }
        return start is null ? total : total + (end - start.Value);
    }
}

/// <summary>An explicit immutable admission of exact configuration bytes; it makes no Running coverage claim.</summary>
public sealed record AssumedBreakConfigurationAdmission(
    string SnapshotIdentity, SiteId SiteId, MachineId MachineId,
    DateOnly EffectiveFrom, DateOnly? EffectiveTo, BreakAuthorityAdmissionMode Mode,
    string AdmittingAuthority, string InstructionReference, ImmutableArray<string> SourceReferences)
    : AssumedBreakAuthorityRecord
{
    public override void Validate()
    {
        _ = AssumedBreakAuthorityV1Codec.Key(SnapshotIdentity, snapshot: true);
        if (SiteId.IsEmpty || MachineId.IsEmpty || EffectiveTo <= EffectiveFrom || !Enum.IsDefined(Mode) ||
            string.IsNullOrWhiteSpace(AdmittingAuthority) || string.IsNullOrWhiteSpace(InstructionReference) ||
            SourceReferences.IsDefaultOrEmpty || SourceReferences.Length > 16384 || SourceReferences.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Invalid assumed-break admission.");
    }

    public void ValidateAgainst(AssumedBreakConfigurationSnapshot snapshot)
    {
        Validate(); snapshot.Validate();
        if (SnapshotIdentity != snapshot.Identity || SiteId != snapshot.SiteId || MachineId != snapshot.MachineId ||
            snapshot.ProductionDate < EffectiveFrom || (EffectiveTo is { } end && snapshot.ProductionDate >= end) ||
            snapshot.ExclusionState == ExclusionManifestState.Missing ||
            snapshot.Exclusions.Any(x => x.Classification == AuthorityExclusionClassification.ActualBreak))
            throw new InvalidDataException("Snapshot is not admissible for this scope, effective period or break authority.");
    }
}

public enum BreakAuthorityPublicationOutcome { Appended, Replayed }
public enum BreakAuthorityReadOutcome { Absent, Found, Corrupt, UnsupportedEncoding, IncomparableEvidence, Ambiguous }
public sealed record BreakAuthorityReadResult<T>(BreakAuthorityReadOutcome Outcome, T? Value)
    where T : AssumedBreakAuthorityRecord;

public interface IAssumedBreakAuthorityStore
{
    ValueTask<BreakAuthorityPublicationOutcome> PublishSnapshotAsync(AssumedBreakConfigurationSnapshot snapshot,
        string expectedIdentity, CancellationToken cancellationToken);
    ValueTask<BreakAuthorityPublicationOutcome> PublishAdmissionAsync(AssumedBreakConfigurationAdmission admission,
        string expectedIdentity, CancellationToken cancellationToken);
    ValueTask<BreakAuthorityReadResult<AssumedBreakConfigurationSnapshot>> ReadSnapshotAsync(string identity, CancellationToken cancellationToken);
    ValueTask<BreakAuthorityReadResult<AssumedBreakConfigurationAdmission>> ReadAdmissionAsync(string identity, CancellationToken cancellationToken);
    ValueTask<BreakAuthorityReadResult<AssumedBreakConfigurationAdmission>> SelectAdmissionAsync(
        string snapshotIdentity, SiteId siteId, MachineId machineId, DateOnly productionDate, CancellationToken cancellationToken);
}
