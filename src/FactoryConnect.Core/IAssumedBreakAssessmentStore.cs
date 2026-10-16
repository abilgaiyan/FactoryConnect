namespace FactoryConnect.Core;

public enum AssumedBreakPublicationOutcome { Appended, Replayed }
public enum AssumedBreakStorageReadOutcome { Absent, Found, Corrupt, UnsupportedEncoding, IncomparableEvidence }
public sealed record AssumedBreakAssessmentReadResult(
    AssumedBreakStorageReadOutcome Outcome, DurableAssumedBreakAssessment? Assessment);

/// <summary>Immutable assessment storage only; publication does not establish source evidence authority.</summary>
public interface IAssumedBreakAssessmentStore
{
    ValueTask<AssumedBreakPublicationOutcome> PublishAsync(DurableAssumedBreakAssessment assessment, CancellationToken cancellationToken);
    ValueTask<AssumedBreakAssessmentReadResult> ReadByIdentityAsync(string identity, CancellationToken cancellationToken);
    ValueTask<AssumedBreakAssessmentReadResult> ReadExactAsync(AssumedBreakAssessmentTarget? target, CancellationToken cancellationToken);
}
