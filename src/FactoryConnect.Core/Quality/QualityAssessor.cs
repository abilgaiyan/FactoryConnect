using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Quality;

public enum QualityPolicyApplicability { Applicable, Missing, OutsideScope, Ambiguous }
public enum QualityAssessmentBasis { None, Measured, PolicyAssumed }
public enum QualityAssessmentReason { Calculated, ProductionIncomplete, ProductionUnproven, ProductionConflicting,
    ZeroProduction, MeasurementPartial, MeasurementConflicting, MeasurementIndeterminate,
    PolicyMissing, PolicyOutsideScope, PolicyAmbiguous }

public sealed record QualityPolicySelection(QualityPolicyApplicability Applicability, ApprovedAllGoodQualityPolicy? Policy);

public static class QualityPolicyApplicabilityEvaluator
{
    public static QualityPolicySelection Evaluate(QualityAssessmentScope scope, IReadOnlyList<ApprovedAllGoodQualityPolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(policies);
        if (policies.Any(static policy => policy is null) ||
            policies.Select(static policy => policy.Identity).Distinct().Count() != policies.Count)
            throw new ArgumentException("Policy versions must be non-null and unique.", nameof(policies));
        var overlapping = policies.Where(policy => policy.SiteId == scope.SiteId && policy.Machines.Contains(scope.MachineId) &&
            policy.EffectiveFromUtc < scope.EndsAtUtc && (policy.EffectiveToUtc is null || policy.EffectiveToUtc > scope.StartsAtUtc)).ToArray();
        // A full-period policy plus a partially overlapping approval is still ambiguous.
        if (overlapping.Length > 1) return new(QualityPolicyApplicability.Ambiguous, null);
        if (overlapping.Length == 1 && overlapping[0].EffectiveFromUtc <= scope.StartsAtUtc &&
            (overlapping[0].EffectiveToUtc is null || overlapping[0].EffectiveToUtc >= scope.EndsAtUtc))
            return new(QualityPolicyApplicability.Applicable, overlapping[0]);
        return new(policies.Count == 0 ? QualityPolicyApplicability.Missing : QualityPolicyApplicability.OutsideScope, null);
    }
}

public sealed class QualityAssessment
{
    internal QualityAssessment(QualityAssessmentTarget target, QualityAssessmentBasis basis, QualityAssessmentReason reason,
        decimal? value = null, decimal? goodQuantity = null, decimal? rejectedQuantity = null, QualityPolicyVersion? appliedPolicy = null)
    {
        Target = target; Identity = QualityAssessmentIdentity.Create(target);
        ContentFingerprint = QualityAssessmentIdentity.ContentFingerprint(target);
        Basis = basis; Reason = reason; Value = value; GoodQuantity = goodQuantity;
        RejectedQuantity = rejectedQuantity; AppliedPolicy = appliedPolicy;
    }
    public QualityAssessmentTarget Target { get; }
    public string Identity { get; }
    public string ContentFingerprint { get; }
    public QualityAssessmentBasis Basis { get; }
    public QualityAssessmentReason Reason { get; }
    public decimal? Value { get; }
    public decimal? GoodQuantity { get; }
    public decimal? RejectedQuantity { get; }
    public QualityPolicyVersion? AppliedPolicy { get; }
    public bool IsAvailable => Value.HasValue;
}

public static class QualityAssessor
{
    public const string RuleVersion = "1.0";
    public static QualityAssessment Assess(QualityAssessmentTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!string.Equals(target.RuleVersion, RuleVersion, StringComparison.Ordinal))
            throw new NotSupportedException("Unsupported Quality assessment rule version.");
        var admissionFailure = target.Production.State switch
        {
            QualityProductionAdmissionState.Incomplete => QualityAssessmentReason.ProductionIncomplete,
            QualityProductionAdmissionState.Unproven => QualityAssessmentReason.ProductionUnproven,
            QualityProductionAdmissionState.Conflicting => QualityAssessmentReason.ProductionConflicting,
            QualityProductionAdmissionState.EstablishedZero => QualityAssessmentReason.ZeroProduction,
            _ => (QualityAssessmentReason?)null,
        };
        if (admissionFailure is { } failed) return Unavailable(target, failed);
        var produced = target.Production.ProducedQuantity!.Value;
        var measured = target.Measurement;
        switch (measured.State)
        {
            case QualityMeasurementState.ExactAuthoritative:
                return new(target, QualityAssessmentBasis.Measured, QualityAssessmentReason.Calculated,
                    measured.GoodQuantity!.Value / produced, measured.GoodQuantity, measured.RejectedQuantity);
            case QualityMeasurementState.Partial:
                return Unavailable(target, QualityAssessmentReason.MeasurementPartial);
            case QualityMeasurementState.Conflicting:
                return Unavailable(target, QualityAssessmentReason.MeasurementConflicting);
            case QualityMeasurementState.Indeterminate:
                return Unavailable(target, QualityAssessmentReason.MeasurementIndeterminate);
        }
        var selection = QualityPolicyApplicabilityEvaluator.Evaluate(target.Scope, target.Policies);
        return selection.Applicability switch
        {
            QualityPolicyApplicability.Applicable => new(target, QualityAssessmentBasis.PolicyAssumed,
                QualityAssessmentReason.Calculated, 1m, produced, 0m, selection.Policy!.Identity),
            QualityPolicyApplicability.Missing => Unavailable(target, QualityAssessmentReason.PolicyMissing),
            QualityPolicyApplicability.OutsideScope => Unavailable(target, QualityAssessmentReason.PolicyOutsideScope),
            _ => Unavailable(target, QualityAssessmentReason.PolicyAmbiguous),
        };
    }

    private static QualityAssessment Unavailable(QualityAssessmentTarget target, QualityAssessmentReason reason) =>
        new(target, QualityAssessmentBasis.None, reason);
}

public enum QualityAssessmentSelectionStatus { Matched, UnavailableMissingAssessment, UnavailableIncomparableEvidence }
public sealed record QualityAssessmentSelection(QualityAssessmentSelectionStatus Status, QualityAssessment? Assessment);

public static class QualityAssessmentSelector
{
    /// <summary>Null target means no comparable authority-selected cut. Never orders opaque revisions or uses creation time.</summary>
    public static QualityAssessmentSelection Select(QualityAssessmentTarget? target, IEnumerable<QualityAssessment> assessments)
    {
        ArgumentNullException.ThrowIfNull(assessments);
        var snapshot = assessments.ToArray();
        if (snapshot.Any(static item => item is null))
            throw new ArgumentException("Assessments must be non-null.", nameof(assessments));
        foreach (var group in snapshot.GroupBy(static item => item.Identity, StringComparer.Ordinal))
            if (group.Select(static item => item.ContentFingerprint).Distinct(StringComparer.Ordinal).Skip(1).Any())
                throw new InvalidDataException("Different assessment content under one identity.");
        if (target is null) return new(QualityAssessmentSelectionStatus.UnavailableIncomparableEvidence, null);
        var identity = QualityAssessmentIdentity.Create(target);
        var matched = snapshot.FirstOrDefault(item => string.Equals(item.Identity, identity, StringComparison.Ordinal));
        if (matched is null) return new(QualityAssessmentSelectionStatus.UnavailableMissingAssessment, null);
        if (!string.Equals(matched.ContentFingerprint, QualityAssessmentIdentity.ContentFingerprint(target), StringComparison.Ordinal))
            throw new InvalidDataException("Target content conflicts with the immutable assessment identity.");
        return new(QualityAssessmentSelectionStatus.Matched, matched);
    }
}
