using FactoryConnect.Core;
using Xunit;

namespace FactoryConnect.Core.Tests;

public sealed class AssumedShiftBreakPolicyTests
{
    [Fact]
    public void CompletedEightHourShiftDeductsThirtyMinutes()
    {
        var result = AssumedShiftBreakPolicy.Evaluate(TimeSpan.FromHours(8), TimeSpan.Zero, true);
        Assert.Equal(TimeSpan.FromMinutes(30), result.AssumedBreakDeduction);
        Assert.Equal(TimeSpan.FromMinutes(450), result.PlannedProductionDuration);
        Assert.False(result.IsForecast);
        Assert.True(result.IsPolicyAssumed);
    }

    [Fact]
    public void OngoingShiftIsForecastNotElapsedAuthority()
    {
        var result = AssumedShiftBreakPolicy.Evaluate(TimeSpan.FromHours(8), TimeSpan.Zero, false);
        Assert.True(result.IsForecast);
        Assert.Equal(TimeSpan.FromMinutes(450), result.PlannedProductionDuration);
    }

    [Fact]
    public void ShortScheduledShiftCapsBreakDeduction()
    {
        var result = AssumedShiftBreakPolicy.Evaluate(TimeSpan.FromMinutes(20), TimeSpan.Zero, true);
        Assert.Equal(TimeSpan.FromMinutes(20), result.AssumedBreakDeduction);
        Assert.Equal(TimeSpan.Zero, result.PlannedProductionDuration);
    }

    [Fact]
    public void OtherExclusionsAreRemovedBeforeAllowance()
    {
        var result = AssumedShiftBreakPolicy.Evaluate(TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(45), true);
        Assert.Equal(TimeSpan.FromMinutes(15), result.AssumedBreakDeduction);
        Assert.Equal(TimeSpan.Zero, result.PlannedProductionDuration);
    }

    [Fact]
    public void UnscheduledPeriodHasNoDefaultDeduction()
    {
        var result = AssumedShiftBreakPolicy.Evaluate(TimeSpan.FromHours(8), TimeSpan.Zero, true, false);
        Assert.Equal(TimeSpan.Zero, result.AssumedBreakDeduction);
        Assert.False(result.IsPolicyAssumed);
    }

    [Fact]
    public void InvalidExclusionsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AssumedShiftBreakPolicy.Evaluate(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(11), true));
    }
}
