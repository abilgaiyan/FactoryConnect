using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class OperationalMetricCoverageCodecTests
{
    [Theory]
    [InlineData(OperationalMetricCoverageClassification.Complete)]
    [InlineData(OperationalMetricCoverageClassification.Incomplete)]
    [InlineData(OperationalMetricCoverageClassification.Unproven)]
    public void EveryClassificationRoundTripsExactly(OperationalMetricCoverageClassification classification)
    {
        var assessment = CoverageTestData.Assessment(classification);
        var subject = OperationalMetricCoverageSubject.FromAssessment(assessment);
        Assert.Equal(subject, OperationalMetricCoverageV1Codec.DecodeSubject(OperationalMetricCoverageV1Codec.EncodeSubject(subject)));
        Assert.Equal(assessment, OperationalMetricCoverageV1Codec.DecodeContent(subject, OperationalMetricCoverageV1Codec.EncodeContent(assessment)));
    }

    [Fact]
    public void UnknownEmptyFragmentsAndProvenanceOrderRemainDistinct()
    {
        var original = CoverageTestData.Assessment();
        var subject = OperationalMetricCoverageSubject.FromAssessment(original);
        var empty = CoverageTestData.Assessment(assessed: [], expected: []);
        Assert.NotEqual(OperationalMetricCoverageV1Codec.EncodeContent(original), OperationalMetricCoverageV1Codec.EncodeContent(empty));
        var start = CoverageTestData.Start;
        var fragments = CoverageTestData.Assessment(assessed: [new(start, start.AddMinutes(1)), new(start.AddMinutes(1), start.AddMinutes(2))]);
        var merged = CoverageTestData.Assessment(assessed: [new(start, start.AddMinutes(2))]);
        Assert.NotEqual(OperationalMetricCoverageV1Codec.EncodeContent(fragments), OperationalMetricCoverageV1Codec.EncodeContent(merged));
        var reversed = CoverageTestData.Assessment(references: original.EvidenceReferences.Reverse().ToArray());
        Assert.NotEqual(OperationalMetricCoverageV1Codec.EncodeContent(original), OperationalMetricCoverageV1Codec.EncodeContent(reversed));
        Assert.Equal(fragments, OperationalMetricCoverageV1Codec.DecodeContent(subject, OperationalMetricCoverageV1Codec.EncodeContent(fragments)));
    }

    [Fact]
    public void ExactUtf16IncludesUnpairedSurrogatesAndLongStrings()
    {
        var assessment = CoverageTestData.Assessment(policy: new string('x', 1000) + "\ud800\udfff\ud800", references:
            [new("kind\ud800", "id\udfff", "revision\ud800")]);
        var subject = OperationalMetricCoverageSubject.FromAssessment(assessment);
        var decoded = OperationalMetricCoverageV1Codec.DecodeSubject(OperationalMetricCoverageV1Codec.EncodeSubject(subject));
        Assert.Equal(subject, decoded);
        Assert.Equal(assessment, OperationalMetricCoverageV1Codec.DecodeContent(decoded, OperationalMetricCoverageV1Codec.EncodeContent(assessment)));
    }

    [Fact]
    public void EveryTruncatedPrefixAndTrailingByteIsRejected()
    {
        var assessment = CoverageTestData.Assessment();
        var subject = OperationalMetricCoverageSubject.FromAssessment(assessment);
        var bytes = OperationalMetricCoverageV1Codec.EncodeContent(assessment);
        for (var length = 0; length < bytes.Length; length++)
        {
            Assert.ThrowsAny<Exception>(() => OperationalMetricCoverageV1Codec.DecodeContent(subject, bytes[..length]));
        }

        Assert.Throws<InvalidDataException>(() => OperationalMetricCoverageV1Codec.DecodeContent(subject, [.. bytes, 0]));
        var identity = OperationalMetricCoverageV1Codec.EncodeSubject(subject);
        for (var length = 0; length < identity.Length; length++)
        {
            Assert.ThrowsAny<Exception>(() => OperationalMetricCoverageV1Codec.DecodeSubject(identity[..length]));
        }
    }

    [Theory]
    [InlineData(4)] // classification
    [InlineData(8)] // reason
    [InlineData(12)] // assessed presence
    public void InvalidDiscriminatorsCannotDefaultToComplete(int offset)
    {
        var assessment = CoverageTestData.Assessment();
        var bytes = OperationalMetricCoverageV1Codec.EncodeContent(assessment);
        bytes[offset] = 255;
        Assert.Throws<InvalidDataException>(() => OperationalMetricCoverageV1Codec.DecodeContent(OperationalMetricCoverageSubject.FromAssessment(assessment), bytes));
    }

    [Fact]
    public void ShiftAndAllContextPartitionsRoundTripWithoutLosingTicks()
    {
        var original = CoverageTestData.Assessment();
        var start = CoverageTestData.Start.AddTicks(17);
        var key = new OperationalMetricEvaluationKey(CoverageTestData.Machine,
            new OperationalMetricPeriodId.Shift(new ShiftOccurrenceId(new SiteId("SITE"), new ShiftScheduleAssignmentId("assignment"),
                new ShiftId("shift"), start, start.AddHours(1))),
            new OperationalMetricDefinitionId("metric\ud800", "version"), new OperationalMetricEvaluationContextKey
            {
                ProductionOrderId = new("order"), OperationId = new("operation"), PartId = new("part"), OperatorId = new("operator"),
            });
        var subject = new OperationalMetricCoverageSubject(original.ProcessorId, key, original.SourceRevision, original.CoveragePolicyVersion);
        var bytes = OperationalMetricCoverageV1Codec.EncodeSubject(subject);
        Assert.Equal(subject, OperationalMetricCoverageV1Codec.DecodeSubject(bytes));
        Assert.Throws<InvalidDataException>(() => OperationalMetricCoverageV1Codec.DecodeSubject([.. bytes, 0]));
    }

    [Fact]
    public void OversizedLengthFailsBeforeAllocation()
    {
        var subject = OperationalMetricCoverageSubject.FromAssessment(CoverageTestData.Assessment());
        var bytes = OperationalMetricCoverageV1Codec.EncodeSubject(subject);
        BitConverter.GetBytes(int.MaxValue).CopyTo(bytes, 4);
        Assert.Throws<InvalidDataException>(() => OperationalMetricCoverageV1Codec.DecodeSubject(bytes));
    }

    [Fact]
    public void MaximumRevisionReplayDoesNotAttemptSuccessorArithmetic()
    {
        byte[] content = [1, 2, 3];
        Assert.Equal(OperationalMetricCoveragePublicationOutcome.Replayed,
            SqlServerOperationalMetricCoverageAssessmentStore.ClassifyProposal(content, content, long.MaxValue, long.MaxValue - 1));
        Assert.Equal(OperationalMetricCoveragePublicationOutcome.ContentConflict,
            SqlServerOperationalMetricCoverageAssessmentStore.ClassifyProposal(content, [4], long.MaxValue, long.MaxValue - 1));
        Assert.Equal(OperationalMetricCoveragePublicationOutcome.PredecessorConflict,
            SqlServerOperationalMetricCoverageAssessmentStore.ClassifyProposal(null, content, long.MaxValue, long.MaxValue - 1));
    }

    [Fact]
    public void RequestChecksSubjectSuccessorAndOverflow()
    {
        var assessment = CoverageTestData.Assessment();
        var subject = OperationalMetricCoverageSubject.FromAssessment(assessment);
        Assert.Throws<ArgumentOutOfRangeException>(() => new OperationalMetricCoverageRevision(0));
        Assert.Throws<OverflowException>(() => new OperationalMetricCoverageRevision(long.MaxValue).Next());
        Assert.Throws<ArgumentException>(() => new OperationalMetricCoveragePublicationRequest(subject, new(2), null, assessment));
        Assert.Throws<ArgumentException>(() => new OperationalMetricCoveragePublicationRequest(subject, new(3), new(1), assessment));
        Assert.Throws<ArgumentException>(() => new OperationalMetricCoveragePublicationRequest(subject, new(1), null, CoverageTestData.Assessment(policy: "other")));
        Assert.Equal(long.MaxValue, new OperationalMetricCoveragePublicationRequest(subject, new(long.MaxValue), new(long.MaxValue - 1), assessment).ProposedRevision.Value);
        Assert.Throws<ArgumentOutOfRangeException>(() => OperationalMetricCoverageReadResult.WithoutAssessment(OperationalMetricCoverageReadOutcome.Found));
    }
}

internal static class CoverageTestData
{
    public static DateTimeOffset Start { get; } = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
    public static MachineId Machine { get; } = new(Guid.Parse("8e3f2b0d-2510-4d83-b4db-9d266741a1f3"));
    public static OperationalMetricCoverageAssessment Assessment(
        OperationalMetricCoverageClassification classification = OperationalMetricCoverageClassification.Unproven,
        IReadOnlyList<OperationalMetricCoverageInterval>? assessed = null,
        IReadOnlyList<OperationalMetricCoverageInterval>? expected = null,
        IReadOnlyList<OperationalMetricCoverageEvidenceReference>? references = null,
        string policy = "coverage-v1", ulong position = 3243)
    {
        var interval = new OperationalMetricCoverageInterval(Start, Start.AddHours(1));
        var established = classification != OperationalMetricCoverageClassification.Unproven;
        var key = new OperationalMetricEvaluationKey(Machine,
            new OperationalMetricPeriodId.ProductionDay(new ProductionDayId(new SiteId("SITE"), new DateOnly(2026, 10, 9))),
            new OperationalMetricDefinitionId("availability", "v1"), OperationalMetricEvaluationContextKey.Unpartitioned);
        return new(new("coverage"), key, new(new("aggregation"), new(Machine, "metrics"), new(position)), policy,
            classification, classification switch
            {
                OperationalMetricCoverageClassification.Complete => OperationalMetricCoverageReason.Reconciled,
                OperationalMetricCoverageClassification.Incomplete => OperationalMetricCoverageReason.CoverageGapEstablished,
                _ => OperationalMetricCoverageReason.HistoricalScheduleAuthorityMissing,
            }, established ? [interval] : assessed, established ? interval.EndsAtUtc : null,
            established ? new("schedule", "1") : null, established ? [interval] : expected,
            classification == OperationalMetricCoverageClassification.Complete ? [interval] : [],
            classification == OperationalMetricCoverageClassification.Incomplete ? [interval] : [], [],
            references ?? [new("raw", "A", "1"), new("raw", "B", "2")]);
    }
}
