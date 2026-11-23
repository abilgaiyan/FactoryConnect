namespace FactoryConnect.Persistence.SqlServer;

/// <summary>
/// Read-only, provisional CNC counter evidence for one commissioned shift occurrence.
/// This classifier does not publish production quantities or OEE inputs.
/// </summary>
public static class CncShiftEvidenceClassifier
{
    public sealed record Reading(
        DateTimeOffset ObservedAtUtc,
        long Position,
        string InstanceId,
        long? CounterValue,
        bool Usable);

    public sealed record Preview(
        long ObservedUnitIncrements,
        long BoundaryAmbiguousIncrements,
        long GapFlaggedIncrements,
        long NonUnitTransitions,
        int UsableReadings,
        bool HasEvidence,
        bool Truncated);

    /// <summary>
    /// The input is acquisition-position ordered and includes a preceding numeric reading
    /// when available. Null/invalid readings do not establish numeric continuity.
    /// No-evidence is distinct from a measured zero.
    /// </summary>
    public static Preview Classify(
        IReadOnlyList<Reading> orderedReadings,
        DateTimeOffset shiftStartsAtUtc,
        DateTimeOffset shiftEndsAtUtc,
        TimeSpan diagnosticGap,
        bool truncated = false)
    {
        ArgumentNullException.ThrowIfNull(orderedReadings);
        if (shiftStartsAtUtc.Offset != TimeSpan.Zero ||
            shiftEndsAtUtc.Offset != TimeSpan.Zero ||
            shiftEndsAtUtc <= shiftStartsAtUtc ||
            shiftEndsAtUtc - shiftStartsAtUtc > TimeSpan.FromDays(1) ||
            diagnosticGap <= TimeSpan.Zero)
            throw new ArgumentException("Invalid UTC shift interval or diagnostic gap.");

        long increments = 0, ambiguous = 0, gaps = 0, nonUnit = 0;
        int usable = 0;
        Reading? previous = null;
        long? previousPosition = null;
        DateTimeOffset? previousObservedAt = null;
        foreach (var reading in orderedReadings)
        {
            if (reading.ObservedAtUtc.Offset != TimeSpan.Zero ||
                (previousPosition.HasValue && reading.Position <= previousPosition.Value))
                throw new ArgumentException("Readings must be UTC and strictly position ordered.");
            if (previousObservedAt.HasValue && reading.ObservedAtUtc < previousObservedAt.Value)
                throw new InvalidDataException("CNC observation timestamps regress in acquisition order.");
            previousPosition = reading.Position;
            previousObservedAt = reading.ObservedAtUtc;
            if (!reading.Usable || reading.CounterValue is null)
            {
                // Never bridge an unusable observation to infer a completed part.
                previous = null;
                continue;
            }
            bool inShift = reading.ObservedAtUtc >= shiftStartsAtUtc &&
                           reading.ObservedAtUtc < shiftEndsAtUtc;
            if (inShift) usable++;
            if (previous is not null && inShift)
            {
                // Compare without subtraction overflow.
                bool unit = previous.CounterValue < long.MaxValue &&
                            reading.CounterValue == previous.CounterValue + 1;
                if (unit)
                {
                    increments++;
                    if (previous.ObservedAtUtc < shiftStartsAtUtc ||
                        previous.ObservedAtUtc >= shiftEndsAtUtc)
                        ambiguous++;
                    if (reading.ObservedAtUtc - previous.ObservedAtUtc > diagnosticGap ||
                        reading.InstanceId != previous.InstanceId)
                        gaps++;
                }
                else if (reading.CounterValue != previous.CounterValue)
                    nonUnit++;
            }
            previous = reading;
        }
        return new Preview(increments, ambiguous, gaps, nonUnit,
            usable, usable > 0, truncated);
    }
}
