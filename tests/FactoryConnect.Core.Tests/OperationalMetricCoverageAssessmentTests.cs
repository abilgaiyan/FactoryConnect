using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Tests;

public sealed class OperationalMetricCoverageAssessmentTests
{
    private static readonly MachineId Machine = new(Guid.Parse("de2fd552-9bc5-45ed-9a7c-0c4a2cd3e9ed"));
    private static readonly DateTimeOffset Start = new(2026, 10, 6, 0, 30, 0, TimeSpan.Zero);
    private static readonly OperationalMetricCoverageInterval Whole = new(Start, Start.AddHours(8));
    private static readonly OperationalMetricCoverageScheduleReference Schedule = new("schedule-cut", "historical-1");
    private static readonly OperationalMetricCoverageEvidenceReference Evidence = new("eligibility", "interval-1", "3243");

    [Fact]
    public void HistoricalUnknownAssessmentPreservesUnknowns()
    {
        var assessment = Create();
        Assert.Equal(OperationalMetricCoverageClassification.Unproven, assessment.Classification);
        Assert.Null(assessment.ExpectedIntervals);
        Assert.Null(assessment.AssessedIntervals);
        Assert.Null(assessment.CompletionBoundaryUtc);
        Assert.Null(assessment.HistoricalScheduleAuthority);
        Assert.Equal(new MetricInputPosition(3243), assessment.SourceRevision.Position);
    }

    [Fact]
    public void UnknownAndEstablishedEmptySetsAreDifferent()
    {
        var unknown = Create();
        var empty = Create(assessed: [], expected: []);
        Assert.NotEqual(unknown, empty);
        Assert.Empty(empty.ExpectedIntervals!);
        Assert.Empty(empty.AssessedIntervals!);
    }

    [Fact]
    public void CompleteAllowsAdjacentClassifiedFragments()
    {
        var split = Start.AddHours(4);
        var assessment = Complete([new(Start, split), new(split, Whole.EndsAtUtc)]);
        Assert.Equal(OperationalMetricCoverageClassification.Complete, assessment.Classification);
        Assert.Equal(2, assessment.ClassifiedIntervals.Count);
    }

    [Fact]
    public void CompleteEstablishedEmptyCoverageIsNotUnknown()
    {
        var assessment = Create(
            classification: OperationalMetricCoverageClassification.Complete,
            reason: OperationalMetricCoverageReason.Reconciled,
            assessed: [], expected: [], boundary: Whole.EndsAtUtc, schedule: Schedule, evidence: [Evidence]);
        Assert.Empty(assessment.ExpectedIntervals!);
        Assert.NotEqual(Create(), assessment);
    }

    [Fact]
    public void IncompleteRequiresContainedExplicitGap()
    {
        var gap = new OperationalMetricCoverageInterval(Start.AddHours(4), Whole.EndsAtUtc);
        var assessment = Create(
            classification: OperationalMetricCoverageClassification.Incomplete,
            reason: OperationalMetricCoverageReason.CoverageGapEstablished,
            assessed: [Whole], expected: [Whole], schedule: Schedule,
            classified: [new(Start, gap.StartsAtUtc)], gaps: [gap], evidence: [Evidence]);
        Assert.Single(assessment.GapIntervals);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void CompleteRejectsMissingStructuralRequirements(int missing)
    {
        Assert.Throws<ArgumentException>(() => Create(
            classification: OperationalMetricCoverageClassification.Complete,
            reason: OperationalMetricCoverageReason.Reconciled,
            assessed: missing == 0 ? null : [Whole],
            expected: missing == 1 ? null : [Whole],
            boundary: missing == 2 ? null : Whole.EndsAtUtc,
            schedule: missing == 3 ? null : Schedule,
            classified: [Whole], evidence: missing == 4 ? [] : [Evidence]));
    }

    [Fact]
    public void CompleteRejectsGapOrIncompleteReconciliation()
    {
        Assert.Throws<ArgumentException>(() => Complete([new(Start, Start.AddHours(7))]));
        Assert.Throws<ArgumentException>(() => Create(
            classification: OperationalMetricCoverageClassification.Complete,
            reason: OperationalMetricCoverageReason.Reconciled,
            assessed: [Whole], expected: [Whole], boundary: Whole.EndsAtUtc,
            schedule: Schedule, gaps: [Whole], evidence: [Evidence]));
    }

    [Fact]
    public void IncompleteRejectsAbsentOrOutOfExtentGap()
    {
        Assert.Throws<ArgumentException>(() => Create(
            classification: OperationalMetricCoverageClassification.Incomplete,
            reason: OperationalMetricCoverageReason.CoverageGapEstablished,
            assessed: [Whole], expected: [Whole], schedule: Schedule, evidence: [Evidence]));
        Assert.Throws<ArgumentException>(() => Create(
            classification: OperationalMetricCoverageClassification.Incomplete,
            reason: OperationalMetricCoverageReason.CoverageGapEstablished,
            assessed: [Whole], expected: [Whole], schedule: Schedule,
            gaps: [new(Whole.EndsAtUtc, Whole.EndsAtUtc.AddHours(1))], evidence: [Evidence]));
    }

    [Fact]
    public void UnknownEligibilityIsExplicitAndNeverComplete()
    {
        var assessment = Create(
            reason: OperationalMetricCoverageReason.EligibilityClassificationUnknown,
            assessed: [Whole], expected: [Whole], schedule: Schedule,
            unknown: [Whole], evidence: [Evidence]);
        Assert.Equal(OperationalMetricCoverageClassification.Unproven, assessment.Classification);
        Assert.Single(assessment.UnknownEligibilityIntervals);
        Assert.Throws<ArgumentException>(() => Create(
            reason: OperationalMetricCoverageReason.EligibilityClassificationUnknown));
    }

    [Fact]
    public void ReconciliationKindsCannotOverlap()
    {
        Assert.Throws<ArgumentException>(() => Create(
            assessed: [Whole], expected: [Whole], classified: [Whole], gaps: [Whole]));
    }

    [Fact]
    public void IntervalOrderAndUtcBoundariesAreValidated()
    {
        Assert.Throws<ArgumentException>(() => new OperationalMetricCoverageInterval(Start, Start));
        Assert.Throws<ArgumentException>(() => new OperationalMetricCoverageInterval(Start.AddHours(1), Start));
        Assert.Throws<ArgumentException>(() => new OperationalMetricCoverageInterval(
            Start.ToOffset(TimeSpan.FromHours(5.5)), Whole.EndsAtUtc));
        Assert.Throws<ArgumentException>(() => Create(assessed: [
            new(Start.AddHours(4), Whole.EndsAtUtc), new(Start, Start.AddHours(4))]));
        Assert.Throws<ArgumentException>(() => Create(boundary: Start.ToOffset(TimeSpan.FromHours(1))));
        Assert.Throws<ArgumentException>(() => Create(assessed: [Whole], boundary: Start.AddHours(7)));
    }

    [Fact]
    public void CallerCollectionMutationCannotChangeAssessment()
    {
        var expected = new List<OperationalMetricCoverageInterval> { Whole };
        var references = new List<OperationalMetricCoverageEvidenceReference> { Evidence };
        var assessment = Create(assessed: expected, expected: expected, classified: expected, evidence: references);
        var equal = Create(assessed: [Whole], expected: [Whole], classified: [Whole], evidence: [Evidence]);
        expected.Clear();
        references.Clear();
        Assert.Equal(equal, assessment);
        Assert.Equal(equal.GetHashCode(), assessment.GetHashCode());
        Assert.Throws<NotSupportedException>(() =>
            ((IList<OperationalMetricCoverageInterval>)assessment.ExpectedIntervals!).Clear());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void EveryIdentityDimensionParticipatesInEquality(int changed)
    {
        var key = Key();
        key = changed switch
        {
            1 => new(Machine, new OperationalMetricPeriodId.ProductionDay(
                new ProductionDayId(new SiteId("CAMPUS-1"), new DateOnly(2026, 10, 7))), key.DefinitionId, key.ContextKey),
            2 => new(Machine, key.PeriodId, new("availability", "3.0"), key.ContextKey),
            3 => new(Machine, key.PeriodId, key.DefinitionId,
                new OperationalMetricEvaluationContextKey { PartId = new PartId("PART-1") }),
            _ => key,
        };
        var revision = new MetricAggregationCheckpoint(
            new(changed == 5 ? "other-aggregation" : "aggregation"),
            new(Machine, changed == 6 ? "other-stream" : "metric-inputs"),
            new(changed == 4 ? 3244UL : 3243UL));
        Assert.NotEqual(Create(), Create(
            processor: new(changed == 0 ? "other-projection" : "builtins-v2"),
            key: key, revision: revision, policy: changed == 7 ? "2.0" : "1.0"));
    }

    [Fact]
    public void SourceMachineMismatchAndBlankPolicyAreRejected()
    {
        var otherMachine = new MachineId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        Assert.Throws<ArgumentException>(() => Create(revision: new(
            new("aggregation"), MetricInputStreamId.ForMachine(otherMachine), new(3243))));
        Assert.Throws<ArgumentException>(() => Create(policy: " "));
    }

    [Fact]
    public void ExactProvenanceOrderAndRevisionAffectEquality()
    {
        var other = new OperationalMetricCoverageEvidenceReference("activity", "period-2", "3243");
        var left = Create(evidence: [Evidence, other]);
        Assert.Equal(left, Create(evidence: [Evidence, other]));
        Assert.NotEqual(left, Create(evidence: [other, Evidence]));
        Assert.NotEqual(Create(evidence: [Evidence]), Create(evidence: [
            new("eligibility", "interval-1", "3244")]));
        Assert.NotEqual(Create(schedule: Schedule, reason: OperationalMetricCoverageReason.ExpectedExtentUnknown), Create(schedule: new("schedule-cut", "historical-2"),
            reason: OperationalMetricCoverageReason.ExpectedExtentUnknown));
    }

    [Fact]
    public void MachineAndShiftIdentityRemainDistinct()
    {
        var otherMachine = new MachineId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var original = Key();
        var otherKey = new OperationalMetricEvaluationKey(otherMachine, original.PeriodId,
            original.DefinitionId, original.ContextKey);
        Assert.NotEqual(Create(), Create(key: otherKey, revision: new(
            new("aggregation"), MetricInputStreamId.ForMachine(otherMachine), new(3243))));
        var shiftKey = new OperationalMetricEvaluationKey(Machine,
            new OperationalMetricPeriodId.Shift(new ShiftOccurrenceId(
                new SiteId("CAMPUS-1"), new ShiftScheduleAssignmentId("SHIFT-1-ASSIGNMENT"),
                new ShiftId("SHIFT-1"), Start, Whole.EndsAtUtc)),
            original.DefinitionId, original.ContextKey);
        Assert.NotEqual(Create(), Create(key: shiftKey));
    }

    [Fact]
    public void ContentAndFragmentOrderingAreExact()
    {
        var whole = Create(assessed: [Whole], expected: [Whole], classified: [Whole]);
        var split = Create(assessed: [Whole], expected: [Whole], classified: [
            new(Start, Start.AddHours(4)), new(Start.AddHours(4), Whole.EndsAtUtc)]);
        Assert.NotEqual(whole, split);
        Assert.NotEqual(whole, Create(assessed: [Whole], expected: [Whole], classified: [Whole],
            boundary: Whole.EndsAtUtc));
        Assert.NotEqual(Create(), Create(reason: OperationalMetricCoverageReason.ExpectedExtentUnknown));
    }

    [Fact]
    public void InvalidReasonClassificationAndDuplicateProvenanceAreRejected()
    {
        Assert.Throws<ArgumentException>(() => Create(reason: OperationalMetricCoverageReason.Reconciled));
        Assert.Throws<ArgumentException>(() => Create(classification: (OperationalMetricCoverageClassification)99));
        Assert.Throws<ArgumentException>(() => Create(reason: (OperationalMetricCoverageReason)99));
        Assert.Throws<ArgumentException>(() => Create(evidence: [Evidence, Evidence]));
        Assert.Throws<ArgumentException>(() => new OperationalMetricCoverageEvidenceReference(" ", "id", "revision"));
        Assert.Throws<ArgumentException>(() => new OperationalMetricCoverageScheduleReference("schedule", " "));
    }

    [Fact]
    public void AssessmentBoundaryUnknownRemainsUnproven()
    {
        var assessment = Create(
            reason: OperationalMetricCoverageReason.AssessmentBoundaryNotEstablished,
            assessed: [Whole], expected: [Whole], schedule: Schedule, classified: [Whole], evidence: [Evidence]);
        Assert.Null(assessment.CompletionBoundaryUtc);
        Assert.Throws<ArgumentException>(() => Create(
            reason: OperationalMetricCoverageReason.AssessmentBoundaryNotEstablished, boundary: Whole.EndsAtUtc));
    }

    private static OperationalMetricCoverageAssessment Complete(
        IEnumerable<OperationalMetricCoverageInterval> classified) =>
        Create(classification: OperationalMetricCoverageClassification.Complete,
            reason: OperationalMetricCoverageReason.Reconciled,
            assessed: [Whole], expected: [Whole], boundary: Whole.EndsAtUtc,
            schedule: Schedule, classified: classified, evidence: [Evidence]);

    private static OperationalMetricEvaluationKey Key() => new(
        Machine,
        new OperationalMetricPeriodId.ProductionDay(new ProductionDayId(new SiteId("CAMPUS-1"), new DateOnly(2026, 10, 6))),
        new OperationalMetricDefinitionId("availability", "2.0"),
        OperationalMetricEvaluationContextKey.Unpartitioned);

    private static OperationalMetricCoverageAssessment Create(
        OperationalMetricProjectionProcessorId? processor = null,
        OperationalMetricEvaluationKey? key = null,
        MetricAggregationCheckpoint? revision = null,
        string policy = "1.0",
        OperationalMetricCoverageClassification classification = OperationalMetricCoverageClassification.Unproven,
        OperationalMetricCoverageReason reason = OperationalMetricCoverageReason.HistoricalScheduleAuthorityMissing,
        IEnumerable<OperationalMetricCoverageInterval>? assessed = null,
        IEnumerable<OperationalMetricCoverageInterval>? expected = null,
        DateTimeOffset? boundary = null,
        OperationalMetricCoverageScheduleReference? schedule = null,
        IEnumerable<OperationalMetricCoverageInterval>? classified = null,
        IEnumerable<OperationalMetricCoverageInterval>? gaps = null,
        IEnumerable<OperationalMetricCoverageInterval>? unknown = null,
        IEnumerable<OperationalMetricCoverageEvidenceReference>? evidence = null) =>
        new(processor ?? new("builtins-v2"), key ?? Key(),
            revision ?? new(new("aggregation"), MetricInputStreamId.ForMachine(Machine), new(3243)),
            policy, classification, reason, assessed, boundary, schedule, expected,
            classified ?? [], gaps ?? [], unknown ?? [], evidence ?? []);
}
