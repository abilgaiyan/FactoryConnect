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
    string CalendarVersion,
    string CalculationRuleVersion,
    string AdmissionMode,
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
            string.IsNullOrWhiteSpace(ScheduleId) || string.IsNullOrWhiteSpace(CalendarVersion) ||
            string.IsNullOrWhiteSpace(CalculationRuleVersion) || string.IsNullOrWhiteSpace(AdmissionMode) ||
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
        if (string.IsNullOrWhiteSpace(AvailabilityReason) ||
            (ObservedRunningDuration is { } running && running < TimeSpan.Zero) ||
            ObservedRunningDuration.HasValue != (Target.RunningEvidenceReference is not null) ||
            AvailabilityPercentage is < 0 or > 100 ||
            (AvailabilityPercentage.HasValue && (!ObservedRunningDuration.HasValue || PlannedTime is null ||
                PlannedTime.IsForecast || PlannedTime.PlannedProductionDuration <= TimeSpan.Zero)) ||
            (PlannedTime is { } planned &&
                (planned.EligibleScheduledDuration < TimeSpan.Zero ||
                 planned.AssumedBreakDeduction < TimeSpan.Zero ||
                 planned.PlannedProductionDuration < TimeSpan.Zero ||
                 planned.EligibleScheduledDuration != planned.AssumedBreakDeduction + planned.PlannedProductionDuration)))
            throw new InvalidDataException("Inconsistent assumed-break assessment result.");
    }
}
