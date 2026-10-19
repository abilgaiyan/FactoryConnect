namespace FactoryConnect.Abstractions;

/// <summary>Publication provenance, not a substitute for historical authority verification.</summary>
public sealed record OperationalMetricPublicationMetadata
{
    public static OperationalMetricPublicationMetadata Legacy { get; } = new(1, null, null, null);
    public OperationalMetricPublicationMetadata(int publicationFormatVersion, string? evaluationPolicyId, string? resultId, string? conflictWitnessId)
    {
        if (publicationFormatVersion == 1)
        {
            if (evaluationPolicyId is not null || resultId is not null || conflictWitnessId is not null)
                throw new ArgumentException("Legacy publication cannot carry containment identities.");
        }
        else if (publicationFormatVersion == 2)
        {
            if (evaluationPolicyId != AvailabilityContainmentV1.PolicyId || !IsDigest(resultId) ||
                (conflictWitnessId is not null && !IsDigest(conflictWitnessId)))
                throw new ArgumentException("Containment publication requires its policy and canonical digest identities.");
        }
        else throw new ArgumentOutOfRangeException(nameof(publicationFormatVersion));
        PublicationFormatVersion = publicationFormatVersion; EvaluationPolicyId = evaluationPolicyId;
        ResultId = resultId; ConflictWitnessId = conflictWitnessId;
    }
    public int PublicationFormatVersion { get; }
    public string? EvaluationPolicyId { get; }
    public string? ResultId { get; }
    public string? ConflictWitnessId { get; }
    private static bool IsDigest(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
}

public enum OperationalMetricReportingReadFailure { UnsupportedCodec, MissingHistoricalAuthority, Integrity }

/// <summary>Allows transport mapping of explicitly classified evidence failures, without classifying arbitrary runtime errors.</summary>
public interface IOperationalMetricReportingReadFailure
{
    OperationalMetricReportingReadFailure ReportingFailure { get; }
}
