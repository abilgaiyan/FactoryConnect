namespace FactoryConnect.Core;

/// <summary>
/// Computes a duration-only, policy-assumed planned-production allowance.
/// It never fabricates break timestamps or changes machine activity intervals.
/// Consumers must not use a forecast as an elapsed Availability denominator.
/// </summary>
public static class AssumedShiftBreakPolicy
{
    public static readonly TimeSpan DefaultAllowance = TimeSpan.FromMinutes(30);

    public static AssumedShiftBreakResult Evaluate(
        TimeSpan scheduledDuration,
        TimeSpan otherExcludedDuration,
        bool isCompleted,
        bool isScheduled = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(scheduledDuration, TimeSpan.Zero);

        ArgumentOutOfRangeException.ThrowIfLessThan(otherExcludedDuration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(otherExcludedDuration, scheduledDuration);

        var eligible = isScheduled ? scheduledDuration - otherExcludedDuration : TimeSpan.Zero;
        var deduction = eligible < DefaultAllowance ? eligible : DefaultAllowance;
        return new AssumedShiftBreakResult(
            eligible,
            deduction,
            eligible - deduction,
            IsForecast: !isCompleted,
            IsPolicyAssumed: isScheduled);
    }
}

public sealed record AssumedShiftBreakResult(
    TimeSpan EligibleScheduledDuration,
    TimeSpan AssumedBreakDeduction,
    TimeSpan PlannedProductionDuration,
    bool IsForecast,
    bool IsPolicyAssumed);
