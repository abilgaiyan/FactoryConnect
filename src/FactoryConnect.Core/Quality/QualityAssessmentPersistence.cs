using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Quality;

public enum QualityPublicationOutcome { Appended, Replayed }
public enum QualityStorageReadOutcome { Found, Absent, IncomparableEvidence, Corrupt, UnsupportedEncoding }
public sealed record QualityAssessmentReadResult(QualityStorageReadOutcome Outcome, QualityAssessment? Assessment);
public sealed record QualityPolicyReadResult(QualityStorageReadOutcome Outcome, ApprovedAllGoodQualityPolicy? Policy);

/// <summary>Immutable publication. Conflicting identity/content throws InvalidDataException without mutation.</summary>
public interface IQualityAssessmentStore
{
    ValueTask<QualityPublicationOutcome> PublishAsync(QualityAssessment assessment, CancellationToken cancellationToken);
    ValueTask<QualityAssessmentReadResult> ReadByIdentityAsync(string identity, CancellationToken cancellationToken);
    ValueTask<QualityAssessmentReadResult> ReadExactAsync(QualityAssessmentTarget? target, CancellationToken cancellationToken);
}

public interface IQualityPolicyStore
{
    ValueTask<QualityPublicationOutcome> PublishPolicyAsync(ApprovedAllGoodQualityPolicy policy, CancellationToken cancellationToken);
    ValueTask<QualityPolicyReadResult> ReadPolicyAsync(QualityPolicyVersion identity, CancellationToken cancellationToken);
}
