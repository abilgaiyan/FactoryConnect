using FactoryConnect.Persistence.SqlServer;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class CncShiftEvidenceClassifierTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 28, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddHours(8);
    private static CncShiftEvidenceClassifier.Reading R(int position, int minutes, long? value,
        bool usable = true, string instance = "one") =>
        new(Start.AddMinutes(minutes), position, instance, value, usable);

    [Fact]
    public void BoundaryCrossingUnitIncrementIsProvisionalAndAmbiguous()
    {
        var result = CncShiftEvidenceClassifier.Classify(
            [R(1, -2, 10), R(2, 3, 11), R(3, 4, 12)],
            Start, End, TimeSpan.FromMinutes(30));
        Assert.Equal(2, result.ObservedUnitIncrements);
        Assert.Equal(1, result.BoundaryAmbiguousIncrements);
        Assert.Equal(0, result.GapFlaggedIncrements);
        Assert.Equal(2, result.UsableReadings);
    }

    [Fact]
    public void NoReadingsIsNoEvidenceNotVerifiedZero()
    {
        var result = CncShiftEvidenceClassifier.Classify(
            [], Start, End, TimeSpan.FromMinutes(30));
        Assert.False(result.HasEvidence);
        Assert.Equal(0, result.ObservedUnitIncrements);
    }

    [Fact]
    public void GapAndInstanceChangeDoNotDoubleCountAnIncrement()
    {
        var result = CncShiftEvidenceClassifier.Classify(
            [R(1, 1, 10), R(2, 45, 11, instance: "two")],
            Start, End, TimeSpan.FromMinutes(30));
        Assert.Equal(1, result.ObservedUnitIncrements);
        Assert.Equal(1, result.GapFlaggedIncrements);
    }

    [Fact]
    public void ResetAndJumpAreNotUnitProduction()
    {
        var result = CncShiftEvidenceClassifier.Classify(
            [R(1, 1, 10), R(2, 2, 3), R(3, 3, 9)],
            Start, End, TimeSpan.FromMinutes(30));
        Assert.Equal(0, result.ObservedUnitIncrements);
        Assert.Equal(2, result.NonUnitTransitions);
    }

    [Fact]
    public void UnusableReadingBreaksCounterContinuity()
    {
        var result = CncShiftEvidenceClassifier.Classify(
            [R(1, 1, 10), R(2, 2, null, usable: false), R(3, 3, 11)],
            Start, End, TimeSpan.FromMinutes(30));
        Assert.Equal(0, result.ObservedUnitIncrements);
        Assert.Equal(2, result.UsableReadings);
    }

    [Fact]
    public void RegressingObservationTimeFailsClosed()
    {
        Assert.Throws<InvalidDataException>(() =>
            CncShiftEvidenceClassifier.Classify(
                [R(1, 5, 10), R(2, 4, 11)],
                Start, End, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void MaximumCounterValueDoesNotOverflowIntoFalseIncrement()
    {
        var result = CncShiftEvidenceClassifier.Classify(
            [R(1, 1, long.MaxValue), R(2, 2, long.MinValue)],
            Start, End, TimeSpan.FromMinutes(30));
        Assert.Equal(0, result.ObservedUnitIncrements);
        Assert.Equal(1, result.NonUnitTransitions);
    }

    [Fact]
    public void InShiftReadingWithoutPredecessorDoesNotInventAnIncrement()
    {
        var result = CncShiftEvidenceClassifier.Classify(
            [R(1, 1, 500)],
            Start, End, TimeSpan.FromMinutes(30));
        Assert.True(result.HasEvidence);
        Assert.Equal(1, result.UsableReadings);
        Assert.Equal(0, result.ObservedUnitIncrements);
    }

    [Fact]
    public void DecimalTwentyPositionBeyondInt64IsOrderedWithoutNarrowing()
    {
        const decimal beyondInt64 = 10000000000000000000m;
        var result = CncShiftEvidenceClassifier.Classify(
            [new(Start.AddMinutes(1), beyondInt64, "one", 10, true),
             new(Start.AddMinutes(2), beyondInt64 + 1, "one", 11, true)],
            Start, End, TimeSpan.FromMinutes(30));
        Assert.Equal(1, result.ObservedUnitIncrements);
    }

    [Fact]
    public void InvalidOrderingAndUtcIntervalAreRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            CncShiftEvidenceClassifier.Classify([R(2, 1, 10), R(1, 2, 11)],
                Start, End, TimeSpan.FromMinutes(30)));
        Assert.Throws<ArgumentException>(() =>
            CncShiftEvidenceClassifier.Classify([], Start, Start,
                TimeSpan.FromMinutes(30)));
    }
}
