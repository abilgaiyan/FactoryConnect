using FactoryConnect.Abstractions;

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
        ArgumentNullException.ThrowIfNull(request.Policy);
        ArgumentNullException.ThrowIfNull(request.Exclusions);
        var policy = request.Policy;

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
            return Unavailable("PolicyNotEstablished", policy, request.ObservedRunningDuration);
        }

        if (!TryUnionExclusions(request, out var excludedDuration))
        {
            return Unavailable("ExclusionAuthorityAmbiguous", policy, request.ObservedRunningDuration);
        }

        var planned = AssumedShiftBreakPolicy.Evaluate(
            request.ShiftEndsAtUtc - request.ShiftStartsAtUtc,
            excludedDuration,
            request.IsCompleted,
            request.IsScheduled);
        var availability = AssumedBreakAvailabilityAdmission.Evaluate(
            planned, request.ObservedRunningDuration);
        return new AssumedBreakReportingAssessment(
            policy.Id, policy.Version, planned, availability.UnavailableReason,
            availability.AvailabilityPercentage, availability.ObservedRunningDuration);
    }

    private static AssumedBreakReportingAssessment Unavailable(
        string reason, AssumedBreakReportingPolicy policy, TimeSpan running) =>
        new(policy.Id, policy.Version, null, reason, null, running);

    private static bool TryUnionExclusions(
        AssumedBreakReportingRequest request, out TimeSpan excludedDuration)
    {
        excludedDuration = TimeSpan.Zero;
        if (request.ShiftEndsAtUtc <= request.ShiftStartsAtUtc)
        {
            return false;
        }

        var intervals = request.Exclusions
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
    IReadOnlyList<AssumedBreakReportingExclusion> Exclusions);

public sealed record AssumedBreakReportingAssessment(
    string PolicyId,
    string PolicyVersion,
    AssumedShiftBreakResult? PlannedTime,
    string AvailabilityReason,
    decimal? AvailabilityPercentage,
    TimeSpan ObservedRunningDuration);
