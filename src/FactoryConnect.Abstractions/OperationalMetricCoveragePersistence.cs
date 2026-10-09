namespace FactoryConnect.Abstractions;

/// <summary>Semantic identity; codec versions and classification are not identity fields.</summary>
public sealed record OperationalMetricCoverageSubject
{
    public OperationalMetricCoverageSubject(OperationalMetricProjectionProcessorId processorId,
        OperationalMetricEvaluationKey evaluationKey, MetricAggregationCheckpoint sourceRevision,
        string coveragePolicyVersion)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(evaluationKey);
        ArgumentNullException.ThrowIfNull(sourceRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(coveragePolicyVersion);
        if (evaluationKey.MachineId != sourceRevision.StreamId.MachineId)
        {
            throw new ArgumentException("Coverage subject source must belong to the evaluation machine.", nameof(sourceRevision));
        }

        ProcessorId = processorId;
        EvaluationKey = evaluationKey;
        SourceRevision = sourceRevision;
        CoveragePolicyVersion = coveragePolicyVersion;
    }

    public OperationalMetricProjectionProcessorId ProcessorId { get; }
    public OperationalMetricEvaluationKey EvaluationKey { get; }
    public MetricAggregationCheckpoint SourceRevision { get; }
    public string CoveragePolicyVersion { get; }

    public static OperationalMetricCoverageSubject FromAssessment(OperationalMetricCoverageAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        return new(assessment.ProcessorId, assessment.EvaluationKey, assessment.SourceRevision, assessment.CoveragePolicyVersion);
    }
}

public sealed record OperationalMetricCoverageRevision
{
    public OperationalMetricCoverageRevision(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        Value = value;
    }

    public long Value { get; }
    public OperationalMetricCoverageRevision Next() => new(checked(Value + 1));
}

/// <summary>Retain this exact proposal across uncertain publication outcomes.</summary>
public sealed class OperationalMetricCoveragePublicationRequest
{
    public OperationalMetricCoveragePublicationRequest(OperationalMetricCoverageSubject subject,
        OperationalMetricCoverageRevision proposedRevision, OperationalMetricCoverageRevision? expectedPredecessor,
        OperationalMetricCoverageAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(proposedRevision);
        ArgumentNullException.ThrowIfNull(assessment);
        if (subject != OperationalMetricCoverageSubject.FromAssessment(assessment))
        {
            throw new ArgumentException("Assessment must match the exact publication subject.", nameof(assessment));
        }

        if (expectedPredecessor is null ? proposedRevision.Value != 1 : proposedRevision != expectedPredecessor.Next())
        {
            throw new ArgumentException("Proposal must be revision one or the checked immediate successor.", nameof(proposedRevision));
        }

        Subject = subject;
        ProposedRevision = proposedRevision;
        ExpectedPredecessor = expectedPredecessor;
        Assessment = assessment;
    }

    public OperationalMetricCoverageSubject Subject { get; }
    public OperationalMetricCoverageRevision ProposedRevision { get; }
    public OperationalMetricCoverageRevision? ExpectedPredecessor { get; }
    public OperationalMetricCoverageAssessment Assessment { get; }
}

public enum OperationalMetricCoverageReadOutcome
{
    Found,
    Absent,
    Unavailable,
    UnsupportedEncoding,
    Corrupt,
}

public sealed class OperationalMetricCoverageReadResult
{
    private OperationalMetricCoverageReadResult(OperationalMetricCoverageReadOutcome outcome, OperationalMetricCoverageAssessment? assessment)
    {
        Outcome = outcome;
        Assessment = assessment;
    }

    public OperationalMetricCoverageReadOutcome Outcome { get; }
    public OperationalMetricCoverageAssessment? Assessment { get; }
    public static OperationalMetricCoverageReadResult Found(OperationalMetricCoverageAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        return new(OperationalMetricCoverageReadOutcome.Found, assessment);
    }

    public static OperationalMetricCoverageReadResult WithoutAssessment(OperationalMetricCoverageReadOutcome outcome)
    {
        if (!Enum.IsDefined(outcome) || outcome == OperationalMetricCoverageReadOutcome.Found)
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        return new(outcome, null);
    }
}

public enum OperationalMetricCoveragePublicationOutcome
{
    Appended,
    Replayed,
    ContentConflict,
    PredecessorConflict,
    SourceRevisionAbsent,
    IntegrityFailure,
    UnsupportedEncoding,
}

public interface IOperationalMetricCoverageAssessmentStore
{
    ValueTask<OperationalMetricCoverageReadResult> ReadExactAsync(OperationalMetricCoverageSubject subject,
        OperationalMetricCoverageRevision revision, CancellationToken cancellationToken);

    /// <summary>Infrastructure errors/cancellation propagate; they do not establish rollback.</summary>
    ValueTask<OperationalMetricCoveragePublicationOutcome> PublishAsync(OperationalMetricCoveragePublicationRequest request,
        CancellationToken cancellationToken);
}
