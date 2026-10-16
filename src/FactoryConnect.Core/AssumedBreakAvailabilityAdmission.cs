namespace FactoryConnect.Core;

/// <summary>
/// Admission boundary for an assumed-break planned duration.
/// Running evidence is preserved separately; a duration-only break does not identify
/// running minutes that can be removed to align the Availability numerator.
/// </summary>
public static class AssumedBreakAvailabilityAdmission
{
    public static AssumedBreakAvailabilityOutcome Evaluate(
        AssumedShiftBreakResult planned,
        TimeSpan observedRunningDuration)
    {
        ArgumentNullException.ThrowIfNull(planned);
        ArgumentOutOfRangeException.ThrowIfLessThan(observedRunningDuration, TimeSpan.Zero);

        if (planned.IsForecast)
        {
            return new AssumedBreakAvailabilityOutcome(
                null, "BreakTimingNotEstablished", observedRunningDuration);
        }

        if (planned.PlannedProductionDuration == TimeSpan.Zero)
        {
            return new AssumedBreakAvailabilityOutcome(
                null, "ZeroDenominator", observedRunningDuration);
        }

        return new AssumedBreakAvailabilityOutcome(
            null, "BreakAttributionNotEstablished", observedRunningDuration);
    }
}

public sealed record AssumedBreakAvailabilityOutcome(
    decimal? AvailabilityPercentage,
    string UnavailableReason,
    TimeSpan ObservedRunningDuration);
