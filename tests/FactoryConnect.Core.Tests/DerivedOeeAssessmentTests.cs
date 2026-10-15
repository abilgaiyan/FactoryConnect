using System.Globalization;
using FactoryConnect.Abstractions;
using FactoryConnect.Core.Oee;
using FactoryConnect.Core.Quality;
using FactoryConnect.Reporting.Tests.Shared;
using Xunit;

namespace FactoryConnect.Core.Tests;

public sealed class DerivedOeeAssessmentTests
{
    [Theory]
    [InlineData(false, "0.6", true)]
    [InlineData(true, "0.54", false)]
    public void MeasuredOrAssumedQualityControlsValueAndProvenance(bool measured, string expected, bool provisional)
    {
        var result = DerivedOeeAssessor.Assess(DerivedOeeTestData.Target(DerivedOeeTestData.Quality(measured)));
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), result.Value);
        Assert.Equal(provisional, result.IsProvisional);
        Assert.Equal(provisional ? "approval-reference" : null, result.AssumedPolicy?.ApprovalReference);
        Assert.Equal(provisional ? "approver" : null, result.AssumedPolicy?.ApprovedBy);
    }

    [Theory]
    [InlineData(DerivedOeeCutState.Unproven, DerivedOeeReason.CoherenceUnproven)]
    [InlineData(DerivedOeeCutState.Conflicting, DerivedOeeReason.CoherenceConflicting)]
    public void MatchingScopeAndNumericDependenciesDoNotEstablishCoherence(DerivedOeeCutState cut, DerivedOeeReason reason)
    {
        var result = DerivedOeeAssessor.Assess(DerivedOeeTestData.Target(state: cut));
        Assert.Null(result.Value); Assert.Equal(reason, result.Reason); Assert.False(result.IsProvisional);
        Assert.Equal("approval-reference", result.AssumedPolicy?.ApprovalReference);
    }

    [Fact]
    public void ScopeProcessorAndRevisionMismatchesFailBeforeCalculation()
    {
        var target = DerivedOeeTestData.Target();
        Assert.Throws<ArgumentException>(() => new DerivedOeeTarget(target.Cut, target.Availability,
            DerivedOeeTestData.Metric(target.Cut.Scope, false, 2), target.Quality, "1.0"));
        var other = DerivedOeeTestData.Target(DerivedOeeTestData.Quality(day: true));
        Assert.Throws<ArgumentException>(() => new DerivedOeeTarget(target.Cut, other.Availability, target.Performance, target.Quality, "1.0"));
        var scope = target.Cut.Scope;
        var changedContext = new QualityAssessmentScope(scope.MachineId, scope.PeriodId, scope.StartsAtUtc, scope.EndsAtUtc,
            new OperationalMetricEvaluationContextKey { PartId = new PartId("different") });
        Assert.Throws<ArgumentException>(() => new DerivedOeeMetricDependency(changedContext, target.Availability.Projection));
        var changedMachine = new QualityAssessmentScope(new MachineId(Guid.NewGuid()), scope.PeriodId, scope.StartsAtUtc, scope.EndsAtUtc, scope.Context);
        Assert.Throws<ArgumentException>(() => new DerivedOeeMetricDependency(changedMachine, target.Availability.Projection));
        var day = other.Cut.Scope;
        var changedInterval = new QualityAssessmentScope(day.MachineId, day.PeriodId, day.StartsAtUtc.AddMinutes(1), day.EndsAtUtc, day.Context);
        var changedDependency = new DerivedOeeMetricDependency(changedInterval, other.Availability.Projection);
        Assert.Throws<ArgumentException>(() => new DerivedOeeTarget(other.Cut, changedDependency, other.Performance, other.Quality, "1.0"));
        var cut = new DerivedOeeEvidenceCut(scope, target.Cut.Decision, DerivedOeeCutState.Established, new("other-processor"),
            target.Cut.MetricRevision, target.Quality.Identity, target.Quality.ContentFingerprint);
        Assert.Throws<ArgumentException>(() => new DerivedOeeTarget(cut, target.Availability, target.Performance, target.Quality, "1.0"));
    }

    [Fact]
    public void QualityIdentityAndContentMustMatchTheExplicitCut()
    {
        var target = DerivedOeeTestData.Target();
        Assert.Throws<ArgumentException>(() => new DerivedOeeTarget(target.Cut, target.Availability, target.Performance,
            DerivedOeeTestData.Quality(revision: "2"), "1.0"));
        Assert.Throws<ArgumentException>(() => new DerivedOeeTarget(target.Cut, target.Availability, target.Performance,
            DerivedOeeTestData.Quality(unavailable: true), "1.0"));
        Assert.Throws<ArgumentException>(() => new DerivedOeeEvidenceCut(target.Cut.Scope,
            DerivedOeeTestData.Ref(QualityEvidenceKind.MeasurementCoverage, "not-coherence"), DerivedOeeCutState.Established,
            target.Cut.ProjectionProcessor, target.Cut.MetricRevision, target.Quality.Identity, target.Quality.ContentFingerprint));
    }

    [Fact]
    public void AllUnavailableReasonsSurviveWithoutDefaults()
    {
        var result = DerivedOeeAssessor.Assess(DerivedOeeTestData.Target(DerivedOeeTestData.Quality(unavailable: true),
            availabilityStatus: OperationalMetricEvaluationStatus.Unavailable, performanceStatus: OperationalMetricEvaluationStatus.InsufficientEvidence));
        Assert.Null(result.Value); Assert.Equal(DerivedOeeReason.DependenciesUnavailable, result.Reason);
        Assert.Equal(3, result.Failures.Count);
        Assert.Contains(result.Failures, item => item.Dependency == "availability" && item.MetricReason == OperationalMetricEvaluationReasonCode.MissingOperand && item.OperandName == "missing-input");
        Assert.Contains(result.Failures, item => item.Dependency == "performance" && item.MetricReason == OperationalMetricEvaluationReasonCode.MissingReferenceTime);
        Assert.Contains(result.Failures, item => item.Dependency == "quality" && item.QualityReason == QualityAssessmentReason.MeasurementIndeterminate);
    }

    [Fact]
    public void NumericZeroIsCalculatedButCannotHideAnUnavailableDependency()
    {
        Assert.Equal(0m, DerivedOeeAssessor.Assess(DerivedOeeTestData.Target(ideal: 0)).Value);
        Assert.Equal(0m, DerivedOeeAssessor.Assess(DerivedOeeTestData.Target(DerivedOeeTestData.Quality(measured: true, good: 0))).Value);
        Assert.Null(DerivedOeeAssessor.Assess(DerivedOeeTestData.Target(ideal: 0, availabilityStatus: OperationalMetricEvaluationStatus.Unavailable)).Value);
        // Availability rounds to numeric zero, while shared running time remains positive.
        Assert.Equal(0m, DerivedOeeAssessor.Assess(DerivedOeeTestData.Target(actual: 0.00000001m, planned: 100, ideal: 0)).Value);
    }

    [Fact]
    public void InvalidCanonicalValuesAndSharedEvidenceAreRejected()
    {
        var target = DerivedOeeTestData.Target(); var original = target.Availability.Projection;
        var tampered = new OperationalMetricProjection(original.ProcessorId, original.Key, original.Status, 0.99m, original.Unit,
            null, null, original.SourceRevision, original.OperandEvidence);
        Assert.Throws<ArgumentException>(() => new DerivedOeeMetricDependency(target.Cut.Scope, tampered));
        Assert.Throws<ArgumentException>(() => new DerivedOeeTarget(target.Cut, target.Availability,
            DerivedOeeTestData.Metric(target.Cut.Scope, false, actual: 70), target.Quality, "1.0"));
        Assert.Throws<ArgumentException>(() => new DerivedOeeMetricDependency(target.Cut.Scope,
            new OperationalMetricProjection(original.ProcessorId, original.Key, original.Status, original.Value, "percent", null, null,
                original.SourceRevision, original.OperandEvidence)));
    }

    [Fact]
    public void PerformanceAboveOneIsNotClampedButOeeAboveOneIsUnavailable()
    {
        Assert.Equal(0.6m, DerivedOeeAssessor.Assess(DerivedOeeTestData.Target(actual: 50, ideal: 60)).Value);
        var result = DerivedOeeAssessor.Assess(DerivedOeeTestData.Target(ideal: 120));
        Assert.Null(result.Value); Assert.Equal(DerivedOeeReason.ProductOutOfRange, result.Reason);
    }

    [Fact]
    public void ChangedReferencesAndRulesCreateNewIdentities()
    {
        var baseline = DerivedOeeIdentity.Create(DerivedOeeTestData.Target());
        Assert.NotEqual(baseline, DerivedOeeIdentity.Create(DerivedOeeTestData.Target(metricRevision: 2)));
        Assert.NotEqual(baseline, DerivedOeeIdentity.Create(DerivedOeeTestData.Target(cutRevision: "2")));
        Assert.NotEqual(baseline, DerivedOeeIdentity.Create(DerivedOeeTestData.Target(DerivedOeeTestData.Quality(revision: "2"))));
        Assert.NotEqual(baseline, DerivedOeeIdentity.Create(DerivedOeeTestData.Target(rule: "2.0")));
        Assert.Throws<NotSupportedException>(() => DerivedOeeAssessor.Assess(DerivedOeeTestData.Target(rule: "2.0")));
    }

    [Fact]
    public void ReplayOrderingCultureAndSelectionAreDeterministic()
    {
        var target = DerivedOeeTestData.Target(); var assessment = DerivedOeeAssessor.Assess(target);
        var original = target.Availability.Projection;
        var reordered = new DerivedOeeMetricDependency(target.Cut.Scope,
            new(original.ProcessorId, original.Key, original.Status, original.Value, original.Unit, null, null, original.SourceRevision, original.OperandEvidence.Reverse()));
        var replay = new DerivedOeeTarget(target.Cut, reordered, target.Performance, target.Quality, target.RuleVersion);
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal(assessment.Identity, DerivedOeeIdentity.Create(replay));
            Assert.Equal(assessment.ContentFingerprint, DerivedOeeIdentity.ContentFingerprint(replay));
        }
        finally { CultureInfo.CurrentCulture = culture; }
        Assert.Same(assessment, DerivedOeeSelector.Select(target, [assessment]));
        Assert.Null(DerivedOeeSelector.Select(DerivedOeeTestData.Target(metricRevision: 2), [assessment]));
        Assert.Null(DerivedOeeSelector.Select(null, [assessment]));
        var corrupt = DerivedOeeAssessor.Assess(DerivedOeeTestData.Target(ideal: 61));
        Assert.Equal(assessment.Identity, corrupt.Identity);
        Assert.Throws<InvalidDataException>(() => DerivedOeeSelector.Select(target, [assessment, corrupt]));
        Assert.Throws<InvalidDataException>(() => DerivedOeeSelector.Select(target, [corrupt]));
    }
}
