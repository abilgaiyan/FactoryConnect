using FactoryConnect.Abstractions;

namespace FactoryConnect.Core;

/// <summary>Exact immutable target for RR03-DP.1; not a SQL publication contract.</summary>
public sealed record AssumedBreakAssessmentTarget(
    SiteId SiteId,
    MachineId MachineId,
    DateOnly ProductionDate,
    DateTimeOffset ShiftStartUtc,
    DateTimeOffset ShiftEndUtc,
    string ScheduleId,
    string ScheduleVersion,
    string CalendarVersion,
    string CalculationRuleVersion,
    string AdmissionMode,
    string AssessmentStage,
    string PolicyId,
    string PolicyVersion,
    DateOnly PolicyEffectiveFrom,
    DateOnly? PolicyEffectiveTo,
    TimeSpan PolicyAllowance,
    string PolicyInstructionReference,
    MetricAggregationCheckpoint AggregationRevision,
    string? RunningEvidenceReference,
    string ExclusionEvidenceFingerprint)
{
    public void Validate()
    {
        if (SiteId.IsEmpty || MachineId.IsEmpty || AggregationRevision is null ||
            AggregationRevision.StreamId.MachineId != MachineId ||
            ShiftStartUtc.Offset != TimeSpan.Zero || ShiftEndUtc.Offset != TimeSpan.Zero ||
            ShiftStartUtc >= ShiftEndUtc || PolicyAllowance < TimeSpan.Zero ||
            PolicyEffectiveFrom > ProductionDate ||
            (PolicyEffectiveTo is { } end && (end <= PolicyEffectiveFrom || ProductionDate >= end)) ||
            string.IsNullOrWhiteSpace(ScheduleId) || string.IsNullOrWhiteSpace(ScheduleVersion) ||
            string.IsNullOrWhiteSpace(CalendarVersion) ||
            string.IsNullOrWhiteSpace(CalculationRuleVersion) || string.IsNullOrWhiteSpace(AdmissionMode) ||
            AssessmentStage is not ("Forecast" or "Completed") ||
            string.IsNullOrWhiteSpace(PolicyId) || string.IsNullOrWhiteSpace(PolicyVersion) ||
            string.IsNullOrWhiteSpace(PolicyInstructionReference) ||
            string.IsNullOrWhiteSpace(ExclusionEvidenceFingerprint) ||
            (RunningEvidenceReference is not null && string.IsNullOrWhiteSpace(RunningEvidenceReference)))
            throw new InvalidDataException("Invalid exact assumed-break assessment target.");
    }
}

public sealed record DurableAssumedBreakAssessment(
    AssumedBreakAssessmentTarget Target,
    AssumedShiftBreakResult? PlannedTime,
    TimeSpan? ObservedRunningDuration,
    string AvailabilityReason,
    decimal? AvailabilityPercentage)
{
    public string Identity => AssumedBreakAssessmentV1Codec.Identity(Target);

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Target);
        Target.Validate();
        var scheduled = Target.ShiftEndUtc - Target.ShiftStartUtc;
        if (AvailabilityPercentage is not null ||
            string.IsNullOrWhiteSpace(AvailabilityReason) ||
            (ObservedRunningDuration is { } running && running < TimeSpan.Zero) ||
            ObservedRunningDuration.HasValue != (Target.RunningEvidenceReference is not null) ||
            (PlannedTime is { } p &&
                (p.EligibleScheduledDuration < TimeSpan.Zero ||
                 p.EligibleScheduledDuration > scheduled ||
                 p.AssumedBreakDeduction !=
                     (p.IsPolicyAssumed
                         ? (p.EligibleScheduledDuration < Target.PolicyAllowance
                             ? p.EligibleScheduledDuration : Target.PolicyAllowance)
                         : TimeSpan.Zero) ||
                 p.PlannedProductionDuration != p.EligibleScheduledDuration - p.AssumedBreakDeduction ||
                 p.IsForecast != (Target.AssessmentStage == "Forecast"))) ||
            (PlannedTime is null && AvailabilityReason is not
                ("PolicyNotEstablished" or "ExclusionAuthorityAmbiguous")) ||
            (PlannedTime is not null && AvailabilityReason !=
                (Target.AssessmentStage == "Forecast" ? "BreakTimingNotEstablished" :
                 PlannedTime.PlannedProductionDuration == TimeSpan.Zero ? "ZeroDenominator" :
                 ObservedRunningDuration is null ? "RunningEvidenceNotEstablished" :
                 "BreakAttributionNotEstablished")))
            throw new InvalidDataException("Inconsistent assumed-break assessment result.");
    }
}
