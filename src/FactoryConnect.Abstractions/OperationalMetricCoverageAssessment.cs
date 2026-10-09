using System.Collections.ObjectModel;

namespace FactoryConnect.Abstractions;

public enum OperationalMetricCoverageClassification
{
    Complete,
    Incomplete,
    Unproven,
}

public enum OperationalMetricCoverageReason
{
    Reconciled,
    HistoricalScheduleAuthorityMissing,
    ExpectedExtentUnknown,
    CoverageGapEstablished,
    EligibilityClassificationUnknown,
    AssessmentBoundaryNotEstablished,
}

/// <summary>A positive half-open UTC interval. This is structural evidence, not an authority claim.</summary>
public sealed record OperationalMetricCoverageInterval
{
    public OperationalMetricCoverageInterval(DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc)
    {
        if (startsAtUtc.Offset != TimeSpan.Zero || endsAtUtc.Offset != TimeSpan.Zero || endsAtUtc <= startsAtUtc)
        {
            throw new ArgumentException("Coverage intervals must be positive and expressed in UTC.", nameof(endsAtUtc));
        }

        StartsAtUtc = startsAtUtc;
        EndsAtUtc = endsAtUtc;
    }

    public DateTimeOffset StartsAtUtc { get; }
    public DateTimeOffset EndsAtUtc { get; }
}

/// <summary>An opaque historical schedule reference; construction does not verify the referenced authority.</summary>
public sealed record OperationalMetricCoverageScheduleReference
{
    public OperationalMetricCoverageScheduleReference(string authorityIdentity, string revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorityIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        AuthorityIdentity = authorityIdentity;
        Revision = revision;
    }

    public string AuthorityIdentity { get; }
    public string Revision { get; }
}

/// <summary>An exact, opaque durable evidence reference, not a mutable current-state lookup.</summary>
public sealed record OperationalMetricCoverageEvidenceReference
{
    public OperationalMetricCoverageEvidenceReference(string evidenceKind, string identity, string revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        EvidenceKind = evidenceKind;
        Identity = identity;
        Revision = revision;
    }

    public string EvidenceKind { get; }
    public string Identity { get; }
    public string Revision { get; }
}

/// <summary>
/// Immutable coverage content bound to one logical projection and source cut.
/// Validation establishes structural consistency only, never historical truth or authority.
/// Null interval sets are unknown; empty sets are established empty.
/// No serialization, persistence, or evaluator integration is selected by this contract.
/// </summary>
public sealed class OperationalMetricCoverageAssessment : IEquatable<OperationalMetricCoverageAssessment>
{
    public OperationalMetricCoverageAssessment(
        OperationalMetricProjectionProcessorId processorId,
        OperationalMetricEvaluationKey evaluationKey,
        MetricAggregationCheckpoint sourceRevision,
        string coveragePolicyVersion,
        OperationalMetricCoverageClassification classification,
        OperationalMetricCoverageReason reason,
        IEnumerable<OperationalMetricCoverageInterval>? assessedIntervals,
        DateTimeOffset? completionBoundaryUtc,
        OperationalMetricCoverageScheduleReference? historicalScheduleAuthority,
        IEnumerable<OperationalMetricCoverageInterval>? expectedIntervals,
        IEnumerable<OperationalMetricCoverageInterval> classifiedIntervals,
        IEnumerable<OperationalMetricCoverageInterval> gapIntervals,
        IEnumerable<OperationalMetricCoverageInterval> unknownEligibilityIntervals,
        IEnumerable<OperationalMetricCoverageEvidenceReference> evidenceReferences)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(evaluationKey);
        ArgumentNullException.ThrowIfNull(sourceRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(coveragePolicyVersion);
        if (evaluationKey.MachineId != sourceRevision.StreamId.MachineId)
        {
            throw new ArgumentException("Coverage source revision must belong to the evaluation machine.", nameof(sourceRevision));
        }

        if (!Enum.IsDefined(classification) || !Enum.IsDefined(reason))
        {
            throw new ArgumentException("Coverage classification or reason is unsupported.", nameof(classification));
        }

        if (completionBoundaryUtc is { Offset: var offset } && offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Completion boundary must be UTC.", nameof(completionBoundaryUtc));
        }

        ProcessorId = processorId;
        EvaluationKey = evaluationKey;
        SourceRevision = sourceRevision;
        CoveragePolicyVersion = coveragePolicyVersion;
        Classification = classification;
        Reason = reason;
        AssessedIntervals = assessedIntervals is null ? null : SnapshotIntervals(assessedIntervals);
        CompletionBoundaryUtc = completionBoundaryUtc;
        HistoricalScheduleAuthority = historicalScheduleAuthority;
        ExpectedIntervals = expectedIntervals is null ? null : SnapshotIntervals(expectedIntervals);
        ClassifiedIntervals = SnapshotIntervals(classifiedIntervals);
        GapIntervals = SnapshotIntervals(gapIntervals);
        UnknownEligibilityIntervals = SnapshotIntervals(unknownEligibilityIntervals);
        ArgumentNullException.ThrowIfNull(evidenceReferences);
        var references = evidenceReferences.ToArray();
        if (references.Any(static item => item is null) || references.Distinct().Count() != references.Length)
        {
            throw new ArgumentException("Evidence references must be non-null and unique.", nameof(evidenceReferences));
        }

        EvidenceReferences = Array.AsReadOnly(references);
        ValidateReconciliation();
    }

    public OperationalMetricProjectionProcessorId ProcessorId { get; }
    public OperationalMetricEvaluationKey EvaluationKey { get; }
    public MetricAggregationCheckpoint SourceRevision { get; }
    public string CoveragePolicyVersion { get; }
    public OperationalMetricCoverageClassification Classification { get; }
    public OperationalMetricCoverageReason Reason { get; }
    public IReadOnlyList<OperationalMetricCoverageInterval>? AssessedIntervals { get; }
    public DateTimeOffset? CompletionBoundaryUtc { get; }
    public OperationalMetricCoverageScheduleReference? HistoricalScheduleAuthority { get; }
    public IReadOnlyList<OperationalMetricCoverageInterval>? ExpectedIntervals { get; }
    public IReadOnlyList<OperationalMetricCoverageInterval> ClassifiedIntervals { get; }
    public IReadOnlyList<OperationalMetricCoverageInterval> GapIntervals { get; }
    public IReadOnlyList<OperationalMetricCoverageInterval> UnknownEligibilityIntervals { get; }
    public IReadOnlyList<OperationalMetricCoverageEvidenceReference> EvidenceReferences { get; }

    public bool Equals(OperationalMetricCoverageAssessment? other) =>
        other is not null &&
        ProcessorId == other.ProcessorId && EvaluationKey == other.EvaluationKey &&
        SourceRevision == other.SourceRevision && CoveragePolicyVersion == other.CoveragePolicyVersion &&
        Classification == other.Classification && Reason == other.Reason &&
        CompletionBoundaryUtc == other.CompletionBoundaryUtc &&
        HistoricalScheduleAuthority == other.HistoricalScheduleAuthority &&
        Same(AssessedIntervals, other.AssessedIntervals) && Same(ExpectedIntervals, other.ExpectedIntervals) &&
        ClassifiedIntervals.SequenceEqual(other.ClassifiedIntervals) && GapIntervals.SequenceEqual(other.GapIntervals) &&
        UnknownEligibilityIntervals.SequenceEqual(other.UnknownEligibilityIntervals) &&
        EvidenceReferences.SequenceEqual(other.EvidenceReferences);

    public override bool Equals(object? obj) => obj is OperationalMetricCoverageAssessment other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ProcessorId);
        hash.Add(EvaluationKey);
        hash.Add(SourceRevision);
        hash.Add(CoveragePolicyVersion, StringComparer.Ordinal);
        hash.Add(Classification);
        hash.Add(Reason);
        hash.Add(CompletionBoundaryUtc);
        hash.Add(HistoricalScheduleAuthority);
        AddIntervals(ref hash, AssessedIntervals);
        AddIntervals(ref hash, ExpectedIntervals);
        AddIntervals(ref hash, ClassifiedIntervals);
        AddIntervals(ref hash, GapIntervals);
        AddIntervals(ref hash, UnknownEligibilityIntervals);
        foreach (var evidence in EvidenceReferences)
        {
            hash.Add(evidence);
        }

        return hash.ToHashCode();
    }

    private static ReadOnlyCollection<OperationalMetricCoverageInterval> SnapshotIntervals(
        IEnumerable<OperationalMetricCoverageInterval> intervals)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        var snapshot = intervals.ToArray();
        for (var index = 0; index < snapshot.Length; index++)
        {
            if (snapshot[index] is null || (index > 0 && snapshot[index - 1].EndsAtUtc > snapshot[index].StartsAtUtc))
            {
                throw new ArgumentException("Intervals must be non-null, ordered and non-overlapping.", nameof(intervals));
            }
        }

        return Array.AsReadOnly(snapshot);
    }

    private void ValidateReconciliation()
    {
        var reconciled = ClassifiedIntervals.Concat(GapIntervals).Concat(UnknownEligibilityIntervals)
            .OrderBy(static item => item.StartsAtUtc).ToArray();
        _ = SnapshotIntervals(reconciled);
        if (ExpectedIntervals is not null &&
            reconciled.Any(interval => !Contained(interval, ExpectedIntervals)))
        {
            throw new ArgumentException("Reconciliation intervals must be contained in expected coverage.");
        }

        if (AssessedIntervals is not null)
        {
            if (reconciled.Any(interval => !Contained(interval, AssessedIntervals)) ||
                (ExpectedIntervals is not null && ExpectedIntervals.Any(interval => !Contained(interval, AssessedIntervals))))
            {
                throw new ArgumentException("Expected and reconciled intervals must be contained in assessed extent.");
            }

            if (CompletionBoundaryUtc is { } boundary && AssessedIntervals.Any(interval => interval.EndsAtUtc > boundary))
            {
                throw new ArgumentException("Assessed extent exceeds its completion boundary.");
            }
        }

        switch (Classification)
        {
            case OperationalMetricCoverageClassification.Complete:
                if (Reason != OperationalMetricCoverageReason.Reconciled || HistoricalScheduleAuthority is null ||
                    AssessedIntervals is null || ExpectedIntervals is null || CompletionBoundaryUtc is null ||
                    GapIntervals.Count != 0 || UnknownEligibilityIntervals.Count != 0 ||
                    EvidenceReferences.Count == 0 || !SameUnion(ExpectedIntervals, ClassifiedIntervals))
                {
                    throw new ArgumentException("Complete coverage requires established authority, extent, boundary and exact classified reconciliation.");
                }

                break;
            case OperationalMetricCoverageClassification.Incomplete:
                if (Reason != OperationalMetricCoverageReason.CoverageGapEstablished ||
                    HistoricalScheduleAuthority is null || ExpectedIntervals is null || AssessedIntervals is null ||
                    GapIntervals.Count == 0 || EvidenceReferences.Count == 0)
                {
                    throw new ArgumentException("Incomplete coverage requires established expected coverage and evidenced contained gaps.");
                }

                break;
            case OperationalMetricCoverageClassification.Unproven:
                var consistent = Reason switch
                {
                    OperationalMetricCoverageReason.HistoricalScheduleAuthorityMissing => HistoricalScheduleAuthority is null,
                    OperationalMetricCoverageReason.ExpectedExtentUnknown => ExpectedIntervals is null || AssessedIntervals is null,
                    OperationalMetricCoverageReason.EligibilityClassificationUnknown => UnknownEligibilityIntervals.Count > 0 && EvidenceReferences.Count > 0,
                    OperationalMetricCoverageReason.AssessmentBoundaryNotEstablished => CompletionBoundaryUtc is null,
                    _ => false,
                };
                if (!consistent)
                {
                    throw new ArgumentException("Unproven coverage requires a consistent missing-authority or classification reason.");
                }

                break;
        }
    }

    private static bool Contained(OperationalMetricCoverageInterval interval, IReadOnlyList<OperationalMetricCoverageInterval> extent) =>
        Normalize(extent).Any(item => item.StartsAtUtc <= interval.StartsAtUtc && item.EndsAtUtc >= interval.EndsAtUtc);

    private static bool SameUnion(IReadOnlyList<OperationalMetricCoverageInterval> left, IReadOnlyList<OperationalMetricCoverageInterval> right) =>
        Normalize(left).SequenceEqual(Normalize(right));

    private static List<OperationalMetricCoverageInterval> Normalize(IReadOnlyList<OperationalMetricCoverageInterval> intervals)
    {
        var result = new List<OperationalMetricCoverageInterval>();
        foreach (var interval in intervals)
        {
            if (result.Count > 0 && result[^1].EndsAtUtc == interval.StartsAtUtc)
            {
                result[^1] = new OperationalMetricCoverageInterval(result[^1].StartsAtUtc, interval.EndsAtUtc);
            }
            else
            {
                result.Add(interval);
            }
        }

        return result;
    }

    private static bool Same(IReadOnlyList<OperationalMetricCoverageInterval>? left, IReadOnlyList<OperationalMetricCoverageInterval>? right) =>
        left is null ? right is null : right is not null && left.SequenceEqual(right);

    private static void AddIntervals(ref HashCode hash, IReadOnlyList<OperationalMetricCoverageInterval>? intervals)
    {
        hash.Add(intervals is not null);
        if (intervals is not null)
        {
            foreach (var interval in intervals)
            {
                hash.Add(interval);
            }
        }
    }
}
