using FactoryConnect.Abstractions;
using System.Security.Cryptography;
using System.Text;

namespace FactoryConnect.Core;

/// <summary>
/// Validates a single effective-dated, explicitly admitted demo break policy and
/// produces a fail-closed result without modifying machine activity evidence.
/// This is an assessment boundary, not a replacement for temporal eligibility.
/// </summary>
public static class AssumedBreakReportingConsumer
{
    public static AssumedBreakReportingAssessment Evaluate(
        AssumedBreakReportingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Exclusions);
        var policy = SelectPolicy(request);
        if (policy is null)
        {
            return Unavailable("PolicyNotEstablished", request, null);
        }

        if (string.IsNullOrWhiteSpace(policy.Id) ||
            string.IsNullOrWhiteSpace(policy.Version) ||
            string.IsNullOrWhiteSpace(policy.InstructionReference) ||
            policy.SiteId.IsEmpty ||
            policy.MachineId.IsEmpty ||
            policy.MachineId != request.MachineId ||
            policy.SiteId != request.SiteId ||
            request.FactoryDate < policy.EffectiveFrom ||
            (policy.EffectiveTo is not null && request.FactoryDate >= policy.EffectiveTo.Value) ||
            !policy.IsDemoProvisional ||
            policy.Allowance != AssumedShiftBreakPolicy.DefaultAllowance)
        {
            return Unavailable("PolicyNotEstablished", request, policy);
        }

        if (!TryUnionExclusions(request, out var excludedDuration))
        {
            return Unavailable("ExclusionAuthorityAmbiguous", request, policy);
        }

        var planned = AssumedShiftBreakPolicy.Evaluate(
            request.ShiftEndsAtUtc - request.ShiftStartsAtUtc,
            excludedDuration,
            request.IsCompleted,
            request.IsScheduled);
        var hasRunningEvidence = !string.IsNullOrWhiteSpace(request.RunningEvidenceReference);
        var availability = hasRunningEvidence
            ? AssumedBreakAvailabilityAdmission.Evaluate(planned, request.ObservedRunningDuration)
            : null;
        var reason = availability?.UnavailableReason ??
            (planned.IsForecast ? "BreakTimingNotEstablished" :
             planned.PlannedProductionDuration == TimeSpan.Zero ? "ZeroDenominator" :
             "RunningEvidenceNotEstablished");
        return new AssumedBreakReportingAssessment(
            policy.Id, policy.Version, planned, reason,
            availability?.AvailabilityPercentage,
            hasRunningEvidence ? request.ObservedRunningDuration : null)
        {
            SiteId = request.SiteId,
            MachineId = request.MachineId,
            FactoryDate = request.FactoryDate,
            ShiftStartsAtUtc = request.ShiftStartsAtUtc,
            ShiftEndsAtUtc = request.ShiftEndsAtUtc,
            ExclusionEvidenceFingerprint = FingerprintEvidence(request.Exclusions),
            RunningEvidenceReference = request.RunningEvidenceReference,
        };
    }

    private static AssumedBreakReportingAssessment Unavailable(
        string reason, AssumedBreakReportingRequest request, AssumedBreakReportingPolicy? policy) =>
        new(policy?.Id ?? string.Empty, policy?.Version ?? string.Empty, null, reason, null,
            string.IsNullOrWhiteSpace(request.RunningEvidenceReference) ? null : request.ObservedRunningDuration)
        {
            SiteId = request.SiteId,
            MachineId = request.MachineId,
            FactoryDate = request.FactoryDate,
            ShiftStartsAtUtc = request.ShiftStartsAtUtc,
            ShiftEndsAtUtc = request.ShiftEndsAtUtc,
            ExclusionEvidenceFingerprint = FingerprintEvidence(request.Exclusions),
            RunningEvidenceReference = request.RunningEvidenceReference,
        };

    private static string FingerprintEvidence(IReadOnlyList<AssumedBreakReportingExclusion> exclusions)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var exclusion in exclusions.OrderBy(static x => x.EvidenceId, StringComparer.Ordinal)
                .ThenBy(static x => x.StartsAtUtc).ThenBy(static x => x.EndsAtUtc)
                .ThenBy(static x => x.IsBreak))
            {
                writer.Write(exclusion.EvidenceId ?? string.Empty);
                writer.Write(exclusion.StartsAtUtc.UtcTicks);
                writer.Write(exclusion.EndsAtUtc.UtcTicks);
                writer.Write(exclusion.IsBreak);
            }
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static AssumedBreakReportingPolicy? SelectPolicy(AssumedBreakReportingRequest request)
    {
        var policies = request.CandidatePolicies ?? (request.Policy is null ? [] : [request.Policy]);
        // Competing eligible policies are not resolved by caller ordering.
        // This demo policy is machine-specific; no site-wide fallback is permitted.
        var eligible = policies.Where(policy => policy is not null &&
            policy.SiteId == request.SiteId &&
            policy.MachineId == request.MachineId &&
            policy.EffectiveFrom <= request.FactoryDate &&
            (policy.EffectiveTo is null || request.FactoryDate < policy.EffectiveTo.Value) &&
            policy.IsDemoProvisional &&
            policy.Allowance == AssumedShiftBreakPolicy.DefaultAllowance &&
            !string.IsNullOrWhiteSpace(policy.Id) &&
            !string.IsNullOrWhiteSpace(policy.Version) &&
            !string.IsNullOrWhiteSpace(policy.InstructionReference)).Take(2).ToArray();
        return eligible.Length == 1 ? eligible[0] : null;
    }

    private static bool TryUnionExclusions(
        AssumedBreakReportingRequest request, out TimeSpan excludedDuration)
    {
        excludedDuration = TimeSpan.Zero;
        if (request.ShiftEndsAtUtc <= request.ShiftStartsAtUtc)
        {
            return false;
        }

        // The same durable evidence ID must never describe conflicting intervals or classifications.
        var evidenceDefinitions = new Dictionary<string, AssumedBreakReportingExclusion>(StringComparer.Ordinal);
        foreach (var exclusion in request.Exclusions)
        {
            if (string.IsNullOrWhiteSpace(exclusion.EvidenceId) ||
                (evidenceDefinitions.TryGetValue(exclusion.EvidenceId, out var previous) &&
                 previous != exclusion))
            {
                return false;
            }

            evidenceDefinitions[exclusion.EvidenceId] = exclusion;
        }

        var intervals = evidenceDefinitions.Values
            .OrderBy(static x => x.StartsAtUtc)
            .ThenBy(static x => x.EndsAtUtc)
            .ToArray();
        DateTimeOffset? currentStart = null;
        DateTimeOffset currentEnd = default;
        foreach (var interval in intervals)
        {
            if (string.IsNullOrWhiteSpace(interval.EvidenceId) ||
                interval.IsBreak ||
                interval.EndsAtUtc <= interval.StartsAtUtc ||
                interval.StartsAtUtc < request.ShiftStartsAtUtc ||
                interval.EndsAtUtc > request.ShiftEndsAtUtc)
            {
                return false;
            }

            if (currentStart is null)
            {
                currentStart = interval.StartsAtUtc;
                currentEnd = interval.EndsAtUtc;
            }
            else if (interval.StartsAtUtc <= currentEnd)
            {
                if (interval.EndsAtUtc > currentEnd)
                {
                    currentEnd = interval.EndsAtUtc;
                }
            }
            else
            {
                excludedDuration += currentEnd - currentStart.Value;
                currentStart = interval.StartsAtUtc;
                currentEnd = interval.EndsAtUtc;
            }
        }

        if (currentStart is not null)
        {
            excludedDuration += currentEnd - currentStart.Value;
        }

        return true;
    }
}

public sealed record AssumedBreakReportingPolicy(
    string Id,
    string Version,
    SiteId SiteId,
    MachineId MachineId,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    TimeSpan Allowance,
    string InstructionReference,
    bool IsDemoProvisional);

public sealed record AssumedBreakReportingExclusion(
    string EvidenceId,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    bool IsBreak);

public sealed record AssumedBreakReportingRequest(
    AssumedBreakReportingPolicy Policy,
    SiteId SiteId,
    MachineId MachineId,
    DateOnly FactoryDate,
    DateTimeOffset ShiftStartsAtUtc,
    DateTimeOffset ShiftEndsAtUtc,
    bool IsCompleted,
    bool IsScheduled,
    TimeSpan ObservedRunningDuration,
    IReadOnlyList<AssumedBreakReportingExclusion> Exclusions)
{
    public IReadOnlyList<AssumedBreakReportingPolicy>? CandidatePolicies { get; init; }
    public string? RunningEvidenceReference { get; init; }
}

public sealed record AssumedBreakReportingAssessment(
    string PolicyId,
    string PolicyVersion,
    AssumedShiftBreakResult? PlannedTime,
    string AvailabilityReason,
    decimal? AvailabilityPercentage,
    TimeSpan? ObservedRunningDuration)
{
    public SiteId { get; init; }
    public MachineId MachineId { get; init; }
    public DateOnly FactoryDate { get; init; }
    public DateTimeOffset ShiftStartsAtUtc { get; init; }
    public DateTimeOffset ShiftEndsAtUtc { get; init; }
    public string ExclusionEvidenceFingerprint { get; init; } = string.Empty;
    public string? RunningEvidenceReference { get; init; }
}
