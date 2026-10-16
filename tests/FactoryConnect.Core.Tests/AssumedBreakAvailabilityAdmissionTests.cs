using FactoryConnect.Core;
using Xunit;

namespace FactoryConnect.Core.Tests;

public sealed class AssumedBreakAvailabilityAdmissionTests
{
    [Theory]
    [InlineData(480)]
    [InlineData(300)]
    [InlineData(0)]
    public void CompletedShiftWithoutBreakAttributionDoesNotInventAvailability(int runningMinutes)
    {
        var planned = AssumedShiftBreakPolicy.Evaluate(TimeSpan.FromMinutes(480), TimeSpan.Zero, true);
        var result = AssumedBreakAvailabilityAdmission.Evaluate(planned, TimeSpan.FromMinutes(runningMinutes));

        Assert.Null(result.AvailabilityPercentage);
        Assert.Equal("BreakAttributionNotEstablished", result.UnavailableReason);
        Assert.Equal(TimeSpan.FromMinutes(runningMinutes), result.ObservedRunningDuration);
    }

    [Fact]
    public void ForecastNeverAdmitsElapsedAvailability()
    {
        var planned = AssumedShiftBreakPolicy.Evaluate(TimeSpan.FromHours(8), TimeSpan.Zero, false);
        var result = AssumedBreakAvailabilityAdmission.Evaluate(planned, TimeSpan.FromHours(2));

        Assert.Null(result.AvailabilityPercentage);
        Assert.Equal("BreakTimingNotEstablished", result.UnavailableReason);
    }

    [Fact]
    public void ZeroPlannedTimeNeverDividesByZero()
    {
        var planned = AssumedShiftBreakPolicy.Evaluate(TimeSpan.FromMinutes(20), TimeSpan.Zero, true);
        var result = AssumedBreakAvailabilityAdmission.Evaluate(planned, TimeSpan.Zero);

        Assert.Null(result.AvailabilityPercentage);
        Assert.Equal("ZeroDenominator", result.UnavailableReason);
    }
}
