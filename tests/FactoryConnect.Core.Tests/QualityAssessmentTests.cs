using System.Globalization;
using FactoryConnect.Abstractions;
using FactoryConnect.Core.Quality;
using Xunit;

namespace FactoryConnect.Core.Tests;

public sealed class QualityAssessmentTests
{
    private static readonly MachineId Machine = new(new Guid("11111111-2222-3333-4444-555555555555"));
    private static readonly DateTimeOffset Start = new(2026, 10, 15, 0, 30, 0, TimeSpan.Zero);
    private static QualityAssessmentScope Scope => new(Machine,
        new OperationalMetricPeriodId.Shift(new ShiftOccurrenceId(new SiteId("SITE"),
            new ShiftScheduleAssignmentId("SCHEDULE"), new ShiftId("SHIFT"), Start, Start.AddHours(8))),
        Start, Start.AddHours(8), OperationalMetricEvaluationContextKey.Unpartitioned);
    private static QualityEvidenceReference Ref(QualityEvidenceKind kind, string id = "evidence", string revision = "r1") =>
        new(kind, "authority", id, revision);
    private static QualityProductionAdmission Production(QualityProductionAdmissionState state = QualityProductionAdmissionState.CompleteAdmitted,
        decimal? produced = 100m, string revision = "r1", IEnumerable<QualityEvidenceReference>? dependencies = null,
        QualityAssessmentScope? scope = null) => new(scope ?? Scope, state, Ref(QualityEvidenceKind.ProductionAdmission, revision: revision),
            "admission-v1", state is QualityProductionAdmissionState.CompleteAdmitted or QualityProductionAdmissionState.EstablishedZero
                ? Ref(QualityEvidenceKind.QuantityPopulation, "population") : null,
            produced, dependencies ?? []);
    private static QualityMeasurementCoverage Measurement(QualityMeasurementState state = QualityMeasurementState.EstablishedAbsent,
        string revision = "r1", QualityAssessmentScope? scope = null) => new(scope ?? Scope, state,
            Ref(QualityEvidenceKind.MeasurementCoverage, revision: revision), null, null, null, null, []);
    private static ApprovedAllGoodQualityPolicy Policy(string version = "1", DateTimeOffset? from = null, DateTimeOffset? to = null,
        SiteId? site = null, IEnumerable<MachineId>? machines = null) => new(new QualityPolicyVersion("POLICY", version),
            site ?? new SiteId("SITE"), machines ?? [Machine], "approval-123", "supervisor", from ?? Start, to);
    private static QualityAssessmentTarget Target(QualityProductionAdmission? production = null, QualityMeasurementCoverage? measurement = null,
        IEnumerable<ApprovedAllGoodQualityPolicy>? policies = null, string rule = QualityAssessor.RuleVersion,
        QualityAssessmentScope? scope = null) => new(scope ?? Scope, production ?? Production(), measurement ?? Measurement(),
            policies ?? [Policy()], rule);
    private static QualityMeasurementCoverage Exact(decimal produced = 100m, decimal good = 90m, decimal rejected = 10m,
        QualityEvidenceReference? population = null, string revision = "r1") => new(Scope, QualityMeasurementState.ExactAuthoritative,
            Ref(QualityEvidenceKind.MeasurementCoverage, revision: revision), population ?? Ref(QualityEvidenceKind.QuantityPopulation, "population"),
            produced, good, rejected, [Ref(QualityEvidenceKind.MeasurementSet, "measured")]);

    [Theory]
    [InlineData(QualityProductionAdmissionState.CompleteAdmitted, 0)]
    [InlineData(QualityProductionAdmissionState.CompleteAdmitted, -1)]
    [InlineData(QualityProductionAdmissionState.EstablishedZero, 1)]
    [InlineData(QualityProductionAdmissionState.EstablishedZero, -1)]
    [InlineData(QualityProductionAdmissionState.Unproven, 100)]
    public void ContradictoryAdmissionIsRejected(QualityProductionAdmissionState state, int produced) =>
        Assert.Throws<ArgumentException>(() => Production(state, produced));

    [Fact]
    public void AdmittedProductionRequiresQuantityAndPopulation()
    {
        Assert.Throws<ArgumentException>(() => Production(produced: null));
        Assert.Throws<ArgumentException>(() => new QualityProductionAdmission(Scope, QualityProductionAdmissionState.CompleteAdmitted,
            Ref(QualityEvidenceKind.ProductionAdmission), "v1", null, 100, []));
    }

    [Theory]
    [InlineData(100, -1, 101)]
    [InlineData(100, 101, -1)]
    [InlineData(-1, 0, -1)]
    [InlineData(100, 90, 9)]
    public void InvalidMeasuredQuantitiesAreRejected(int produced, int good, int rejected) =>
        Assert.Throws<ArgumentException>(() => Exact(produced, good, rejected));

    [Fact]
    public void MeasurementMustMatchAdmittedPopulationAndTotal()
    {
        Assert.Throws<ArgumentException>(() => Target(measurement: Exact(population: Ref(QualityEvidenceKind.QuantityPopulation, "other"))));
        Assert.Throws<ArgumentException>(() => Target(measurement: Exact(99, 90, 9)));
        Assert.Throws<ArgumentException>(() => new QualityMeasurementCoverage(Scope, QualityMeasurementState.ExactAuthoritative,
            Ref(QualityEvidenceKind.MeasurementCoverage), Ref(QualityEvidenceKind.QuantityPopulation), 100, 90, 10, []));
    }

    [Fact]
    public void EstablishedAbsenceRequiresAuthorityOwnedCoverageReferenceAndExactScope()
    {
        Assert.Throws<ArgumentException>(() => new QualityEvidenceReference(QualityEvidenceKind.MeasurementCoverage, "", "id", "r1"));
        Assert.Throws<ArgumentException>(() => new QualityEvidenceReference(QualityEvidenceKind.MeasurementCoverage, "owner", "id", ""));
        Assert.Throws<ArgumentException>(() => new QualityMeasurementCoverage(Scope, QualityMeasurementState.EstablishedAbsent,
            Ref(QualityEvidenceKind.SourceDependency), null, null, null, null, []));
        var other = new QualityAssessmentScope(new MachineId(Guid.NewGuid()), Scope.PeriodId, Start, Start.AddHours(8), Scope.Context);
        Assert.Throws<ArgumentException>(() => Target(measurement: Measurement(scope: other)));
        Assert.Throws<ArgumentException>(() => new QualityMeasurementCoverage(Scope, QualityMeasurementState.EstablishedAbsent,
            Ref(QualityEvidenceKind.MeasurementCoverage), null, null, 0, 0, []));
    }

    [Theory]
    [InlineData(QualityProductionAdmissionState.Incomplete, QualityAssessmentReason.ProductionIncomplete)]
    [InlineData(QualityProductionAdmissionState.Unproven, QualityAssessmentReason.ProductionUnproven)]
    [InlineData(QualityProductionAdmissionState.Conflicting, QualityAssessmentReason.ProductionConflicting)]
    public void NonAdmittedProductionBlocksQuality(QualityProductionAdmissionState state, QualityAssessmentReason reason)
    {
        var result = QualityAssessor.Assess(Target(production: Production(state, null)));
        AssertUnavailable(result, reason);
    }

    [Fact]
    public void EstablishedZeroCannotProduceQualityEvenWithExactMeasurement()
    {
        var zero = Production(QualityProductionAdmissionState.EstablishedZero, 0);
        AssertUnavailable(QualityAssessor.Assess(Target(production: zero)), QualityAssessmentReason.ZeroProduction);
        AssertUnavailable(QualityAssessor.Assess(Target(production: zero, measurement: Exact(0, 0, 0))), QualityAssessmentReason.ZeroProduction);
    }

    [Theory]
    [InlineData(QualityMeasurementState.Partial, QualityAssessmentReason.MeasurementPartial)]
    [InlineData(QualityMeasurementState.Conflicting, QualityAssessmentReason.MeasurementConflicting)]
    [InlineData(QualityMeasurementState.Indeterminate, QualityAssessmentReason.MeasurementIndeterminate)]
    public void UnresolvedMeasurementBlocksAssumption(QualityMeasurementState state, QualityAssessmentReason reason) =>
        AssertUnavailable(QualityAssessor.Assess(Target(measurement: Measurement(state))), reason);

    [Fact]
    public void ExactMeasurementWinsEvenWithoutApplicablePolicy()
    {
        var result = QualityAssessor.Assess(Target(measurement: Exact(), policies: []));
        Assert.Equal(QualityAssessmentBasis.Measured, result.Basis);
        Assert.Equal(0.9m, result.Value);
        Assert.Equal(90m, result.GoodQuantity);
        Assert.Equal(10m, result.RejectedQuantity);
        Assert.Null(result.AppliedPolicy);
    }

    [Fact]
    public void EstablishedAbsenceAndApprovalProduceSeparateAllGoodAssessment()
    {
        var target = Target();
        var result = QualityAssessor.Assess(target);
        Assert.True(result.IsAvailable);
        Assert.Equal(QualityAssessmentBasis.PolicyAssumed, result.Basis);
        Assert.Equal(1m, result.Value);
        Assert.Equal(100m, result.GoodQuantity);
        Assert.Equal(0m, result.RejectedQuantity);
        Assert.Equal(Policy().Identity, result.AppliedPolicy);
        Assert.Null(target.Measurement.GoodQuantity);
        Assert.Equal(QualityMeasurementState.EstablishedAbsent, target.Measurement.State);
    }

    [Theory]
    [InlineData(0, 8, QualityPolicyApplicability.Applicable)]
    [InlineData(-1, 9, QualityPolicyApplicability.Applicable)]
    [InlineData(1, 9, QualityPolicyApplicability.OutsideScope)]
    [InlineData(-1, 7, QualityPolicyApplicability.OutsideScope)]
    [InlineData(-8, 0, QualityPolicyApplicability.OutsideScope)]
    [InlineData(8, 16, QualityPolicyApplicability.OutsideScope)]
    public void PolicyUsesWholeHalfOpenPeriod(int fromHours, int toHours, QualityPolicyApplicability expected)
    {
        var policy = Policy(from: Start.AddHours(fromHours), to: Start.AddHours(toHours));
        Assert.Equal(expected, QualityPolicyApplicabilityEvaluator.Evaluate(Scope, [policy]).Applicability);
    }

    [Fact]
    public void PolicyMustMatchSiteMachineAndHaveUnambiguousCoverage()
    {
        AssertUnavailable(QualityAssessor.Assess(Target(policies: [])), QualityAssessmentReason.PolicyMissing);
        AssertUnavailable(QualityAssessor.Assess(Target(policies: [Policy(site: new SiteId("OTHER"))])), QualityAssessmentReason.PolicyOutsideScope);
        AssertUnavailable(QualityAssessor.Assess(Target(policies: [Policy(machines: [new MachineId(Guid.NewGuid())])])), QualityAssessmentReason.PolicyOutsideScope);
        AssertUnavailable(QualityAssessor.Assess(Target(policies: [Policy(), Policy("2", from: Start.AddHours(1))])), QualityAssessmentReason.PolicyAmbiguous);
        Assert.Equal(1m, QualityAssessor.Assess(Target(policies: [Policy(), Policy("2", from: Start.AddHours(8))])).Value);
    }

    [Fact]
    public void PolicyRequiresExplicitApprovalAndValidUtcBoundaries()
    {
        Assert.Throws<ArgumentException>(() => new ApprovedAllGoodQualityPolicy(new("p", "1"), new("SITE"), [Machine], "", "approver", Start, null));
        Assert.Throws<ArgumentException>(() => Policy(machines: []));
        Assert.Throws<ArgumentException>(() => Policy(machines: [Machine, Machine]));
        Assert.Throws<ArgumentException>(() => Policy(to: Start));
        Assert.Throws<ArgumentException>(() => Policy(from: Start.ToOffset(TimeSpan.FromHours(5.5))));
        Assert.Throws<ArgumentException>(() => new QualityAssessmentScope(Machine, Scope.PeriodId, Start.AddMinutes(1), Start.AddHours(8), Scope.Context));
    }

    [Fact]
    public void InputsAreDefensivelySnapshotted()
    {
        var dependencies = new List<QualityEvidenceReference> { Ref(QualityEvidenceKind.SourceDependency) };
        var machines = new List<MachineId> { Machine };
        var policies = new List<ApprovedAllGoodQualityPolicy> { Policy(machines: machines) };
        var target = Target(production: Production(dependencies: dependencies), policies: policies);
        var fingerprint = QualityAssessmentIdentity.ContentFingerprint(target);
        dependencies.Clear(); machines.Clear(); policies.Clear();
        Assert.Equal(fingerprint, QualityAssessmentIdentity.ContentFingerprint(target));
        Assert.Single(target.Production.Dependencies);
        Assert.Throws<ArgumentException>(() => Target(policies: [Policy(), Policy()]));
    }

    [Fact]
    public void IdentityIsOrderIndependentCultureIndependentAndNormalizesDecimalScale()
    {
        var a = Ref(QualityEvidenceKind.SourceDependency, "a");
        var b = Ref(QualityEvidenceKind.SourceDependency, "b");
        var first = Target(production: Production(produced: 100.00m, dependencies: [a, b]), policies: [Policy(), Policy("2", from: Start.AddDays(1))]);
        var second = Target(production: Production(dependencies: [b, a]), policies: [Policy("2", from: Start.AddDays(1)), Policy()]);
        var id = QualityAssessmentIdentity.Create(first);
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal(id, QualityAssessmentIdentity.Create(second));
            Assert.Equal(QualityAssessmentIdentity.ContentFingerprint(first), QualityAssessmentIdentity.ContentFingerprint(second));
        }
        finally { CultureInfo.CurrentCulture = culture; }
        Assert.StartsWith("quality-assessment:v1:", id, StringComparison.Ordinal);
        using var stream = new MemoryStream(QualityAssessmentIdentity.Encode(first, false));
        using var reader = new BinaryReader(stream);
        Assert.Equal("FactoryConnect.QualityAssessment", reader.ReadString());
        Assert.Equal(1, reader.ReadInt32());
        Assert.False(reader.ReadBoolean());
    }

    [Fact]
    public void CanonicalIdentityHasStableV1GoldenVector() =>
        Assert.Equal("quality-assessment:v1:CF29718E7CBA5BCA3C910E12BC3646328AF4011993FB99D0D441115604D7D930", QualityAssessmentIdentity.Create(Target()));

    [Fact]
    public void EveryReferenceAndRuleChangeCreatesNewIdentity()
    {
        var baseline = QualityAssessmentIdentity.Create(Target());
        Assert.NotEqual(baseline, QualityAssessmentIdentity.Create(Target(production: Production(revision: "r2"))));
        Assert.NotEqual(baseline, QualityAssessmentIdentity.Create(Target(measurement: Measurement(revision: "r2"))));
        Assert.NotEqual(baseline, QualityAssessmentIdentity.Create(Target(policies: [Policy("2")])));
        Assert.NotEqual(baseline, QualityAssessmentIdentity.Create(Target(policies: [])));
        Assert.NotEqual(baseline, QualityAssessmentIdentity.Create(Target(rule: "2.0")));
        Assert.NotEqual(baseline, QualityAssessmentIdentity.Create(Target(production: Production(dependencies: [Ref(QualityEvidenceKind.SourceDependency)]))));
        Assert.Throws<NotSupportedException>(() => QualityAssessor.Assess(Target(rule: "2.0")));
    }

    [Fact]
    public void TypedContextAndPeriodIdentitiesCannotAlias()
    {
        var contextA = new OperationalMetricEvaluationContextKey { PartId = new PartId("same") };
        var contextB = new OperationalMetricEvaluationContextKey { OperationId = new OperationId("same") };
        QualityAssessmentScope With(OperationalMetricPeriodId period, OperationalMetricEvaluationContextKey context) =>
            new(Machine, period, Start, Start.AddHours(8), context);
        QualityAssessmentTarget For(QualityAssessmentScope scope) => Target(scope: scope, production: Production(scope: scope), measurement: Measurement(scope: scope));
        var a = With(Scope.PeriodId, contextA);
        var b = With(Scope.PeriodId, contextB);
        var day = With(new OperationalMetricPeriodId.ProductionDay(new ProductionDayId(new SiteId("SITE"), new DateOnly(2026, 10, 15))), contextA);
        Assert.NotEqual(QualityAssessmentIdentity.Create(For(a)), QualityAssessmentIdentity.Create(For(b)));
        Assert.NotEqual(QualityAssessmentIdentity.Create(For(a)), QualityAssessmentIdentity.Create(For(day)));
        Assert.NotEqual(QualityAssessmentIdentity.Create(For(a)), QualityAssessmentIdentity.Create(Target()));
    }

    [Fact]
    public void ExactTargetSelectionNeverFallsBackToOlderEvidence()
    {
        var old = QualityAssessor.Assess(Target());
        var nextTarget = Target(production: Production(revision: "r2"));
        var next = QualityAssessor.Assess(nextTarget);
        var selected = QualityAssessmentSelector.Select(nextTarget, [next, old]);
        Assert.Equal(QualityAssessmentSelectionStatus.Matched, selected.Status);
        Assert.Same(next, selected.Assessment);
        Assert.Equal(QualityAssessmentSelectionStatus.UnavailableMissingAssessment, QualityAssessmentSelector.Select(nextTarget, [old]).Status);
        Assert.Equal(QualityAssessmentSelectionStatus.UnavailableIncomparableEvidence, QualityAssessmentSelector.Select(null, [next, old]).Status);
        Assert.Same(old, QualityAssessmentSelector.Select(Target(), [next, old]).Assessment);
    }

    [Fact]
    public void ReplayIsIdenticalAndConflictingContentUnderOneIdentityFails()
    {
        var target = Target();
        var first = QualityAssessor.Assess(target);
        var replay = QualityAssessor.Assess(Target());
        Assert.Equal(first.Identity, replay.Identity);
        Assert.Equal(first.ContentFingerprint, replay.ContentFingerprint);
        Assert.Equal(first.Value, replay.Value);
        Assert.Same(first, QualityAssessmentSelector.Select(target, [first, replay]).Assessment);
        var corrupt = QualityAssessor.Assess(Target(production: Production(produced: 101)));
        Assert.Equal(first.Identity, corrupt.Identity);
        Assert.Throws<InvalidDataException>(() => QualityAssessmentSelector.Select(target, [first, corrupt]));
        Assert.Throws<InvalidDataException>(() => QualityAssessmentSelector.Select(target, [corrupt]));
        Assert.Throws<InvalidDataException>(() => QualityAssessmentSelector.Select(null, [first, corrupt]));
    }

    private static void AssertUnavailable(QualityAssessment result, QualityAssessmentReason reason)
    {
        Assert.False(result.IsAvailable);
        Assert.Null(result.Value); Assert.Null(result.GoodQuantity); Assert.Null(result.RejectedQuantity);
        Assert.Null(result.AppliedPolicy);
        Assert.Equal(QualityAssessmentBasis.None, result.Basis);
        Assert.Equal(reason, result.Reason);
    }
}
