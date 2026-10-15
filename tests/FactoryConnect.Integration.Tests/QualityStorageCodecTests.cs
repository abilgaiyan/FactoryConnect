using FactoryConnect.Core.Quality;
using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class QualityStorageCodecTests
{
    [Theory]
    [InlineData(false, false, 100)]
    [InlineData(true, false, 100)]
    [InlineData(false, true, 100)]
    [InlineData(true, true, 0)]
    public void AssessmentRoundTripsExactIdentityContentAndResult(bool measured, bool day, int produced)
    {
        var assessment = QualityAssessor.Assess(QualitySqlTestData.Target(produced: produced, measured: measured, day: day));
        var bytes = QualityStorageV1Codec.EncodeAssessment(assessment);
        var decoded = QualityStorageV1Codec.DecodeAssessment(bytes);
        Assert.Equal(bytes, QualityStorageV1Codec.EncodeAssessment(decoded));
        Assert.Equal(assessment.Identity, decoded.Identity);
        Assert.Equal(assessment.ContentFingerprint, decoded.ContentFingerprint);
        Assert.Equal(assessment.Basis, decoded.Basis); Assert.Equal(assessment.Reason, decoded.Reason);
        Assert.Equal(assessment.Value, decoded.Value);
    }

    [Fact]
    public void PolicyRoundTripsAndRejectsNonCanonicalOrTruncatedBytes()
    {
        var policy = QualitySqlTestData.Policy();
        var bytes = QualityStorageV1Codec.EncodePolicy(policy);
        var decoded = QualityStorageV1Codec.DecodePolicy(bytes);
        Assert.Equal(policy.Identity, decoded.Identity);
        Assert.Equal(policy.ApprovalReference, decoded.ApprovalReference);
        Assert.Equal(bytes, QualityStorageV1Codec.EncodePolicy(decoded));
        Assert.Throws<InvalidDataException>(() => QualityStorageV1Codec.DecodePolicy([.. bytes, 0]));
        Assert.Throws<InvalidDataException>(() => QualityStorageV1Codec.DecodePolicy(bytes[..^1]));
        Assert.Throws<InvalidDataException>(() => QualityStorageV1Codec.DecodePolicy([]));
    }

    [Fact]
    public void AssessmentRejectsTruncationTrailingBytesAndChangedResult()
    {
        var bytes = QualityStorageV1Codec.EncodeAssessment(QualityAssessor.Assess(QualitySqlTestData.Target()));
        Assert.Throws<InvalidDataException>(() => QualityStorageV1Codec.DecodeAssessment(bytes[..^1]));
        Assert.Throws<InvalidDataException>(() => QualityStorageV1Codec.DecodeAssessment([.. bytes, 0]));
        // Last byte belongs to applied policy version; changing it breaks result provenance.
        bytes[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => QualityStorageV1Codec.DecodeAssessment(bytes));
    }
}
