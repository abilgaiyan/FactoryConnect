using System.Diagnostics;

namespace FactoryConnect.Integration.Tests;

public sealed class ReportingGateCriticalCoverageTests
{
    [Fact]
    public void DenseSamplesAndTargetObservationsCoverTheCriticalInterval()
    {
        var samples = new[] { At(300), At(800), At(1300), At(1800), At(2300), At(2800) };
        var observations = new[] { At(250), At(750), At(1250), At(1750), At(2250), At(2750) };
        var result = ReportingGateCriticalCoverage.Assess(At(0), At(3000), samples, observations);
        Assert.True(result.Accepted);
        Assert.Equal("accepted", result.Reason);
        Assert.Equal(At(250), result.CoveredFrom);
        Assert.Equal(At(2750), result.CoveredThrough);
        Assert.Equal(500d, result.MaximumSuccessfulSampleGapMs);
        Assert.Equal(500d, result.MaximumObservationGapMs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingSamplesOrOnlyOutsideSamplesDoNotEstablishCoverage(bool outside)
    {
        long[] samples = outside ? [At(-250), At(3250)] : [];
        var result = ReportingGateCriticalCoverage.Assess(At(0), At(3000), samples, samples);
        Assert.False(result.Accepted);
        Assert.Equal("no-successful-sample-in-critical-interval", result.Reason);
        Assert.Null(result.CoveredFrom);
        Assert.Null(result.CoveredThrough);
    }

    [Fact]
    public void SuccessfulQueriesWithoutCriticalSessionEvidenceAreInsufficient()
    {
        var result = ReportingGateCriticalCoverage.Assess(At(0), At(1000),
            [At(250), At(750)], []);
        Assert.False(result.Accepted);
        Assert.Equal("critical-session-not-observed", result.Reason);
    }

    [Theory]
    [InlineData("leading")]
    [InlineData("middle")]
    [InlineData("trailing")]
    public void AnExceptionFreeMonitorWithLargeGapsIsRejected(string gap)
    {
        long[] points = gap switch
        {
            "leading" => [At(1500), At(2000), At(2500)],
            "middle" => [At(250), At(750), At(2250), At(2750)],
            _ => [At(250), At(750), At(1250)],
        };
        var result = ReportingGateCriticalCoverage.Assess(At(0), At(3000), points, points);
        Assert.False(result.Accepted);
        Assert.Equal("critical-sample-gap-exceeded", result.Reason);
        Assert.True(result.MaximumSuccessfulSampleGapMs > result.MaximumAllowedGapMs);
    }

    [Fact]
    public void DuplicateOrUnsortedSamplesCannotHideAnObservationGap()
    {
        long[] points = [At(2750), At(250), At(250), At(2750)];
        var result = ReportingGateCriticalCoverage.Assess(At(0), At(3000), points, points);
        Assert.False(result.Accepted);
        Assert.Equal(2, result.SuccessfulSamplesInInterval);
        Assert.Equal(2500d, result.MaximumObservationGapMs);
    }

    [Fact]
    public void MissingCriticalIntervalFailsClosed()
    {
        var result = ReportingGateCriticalCoverage.Assess(0, 0, [At(250)], [At(250)]);
        Assert.False(result.Accepted);
        Assert.Equal("critical-interval-missing-or-invalid", result.Reason);
    }

    private static long At(int milliseconds) =>
        Stopwatch.Frequency + (long)milliseconds * Stopwatch.Frequency / 1000;
}
