using System.Diagnostics;

namespace FactoryConnect.Integration.Tests;

// Diagnostic policy only; no production timing or SQL behavior is changed.
internal static class ReportingGateCriticalCoverage
{
    internal static readonly TimeSpan MaximumGap = TimeSpan.FromSeconds(1);

    internal sealed record Assessment(bool Accepted, string Reason, long CriticalStartedAt, long CriticalEndedAt,
        double CriticalDurationMs, double MaximumAllowedGapMs, int SuccessfulSamplesInInterval,
        int CriticalObservationsInInterval, long? CoveredFrom, long? CoveredThrough,
        double LeadingGapMs, double MaximumSuccessfulSampleGapMs, double MaximumObservationGapMs,
        double TrailingGapMs);

    internal static Assessment Assess(long started, long ended, IReadOnlyList<long> successfulSamples,
        IReadOnlyList<long> criticalObservations)
    {
        if (started <= 0 || ended <= started)
            return new(false, "critical-interval-missing-or-invalid", started, ended, 0,
                MaximumGap.TotalMilliseconds, 0, 0, null, null, 0, 0, 0, 0);
        var samples = successfulSamples.Where(t => t >= started && t <= ended).Distinct().Order().ToArray();
        var observations = criticalObservations.Where(t => t >= started && t <= ended).Distinct().Order().ToArray();
        var duration = Milliseconds(started, ended);
        var leading = observations.Length == 0 ? duration : Milliseconds(started, observations[0]);
        var trailing = observations.Length == 0 ? duration : Milliseconds(observations[^1], ended);
        var sampleGap = MaxGap(started, ended, samples);
        var observationGap = MaxGap(started, ended, observations);
        var reason = samples.Length == 0 ? "no-successful-sample-in-critical-interval" :
            observations.Length == 0 ? "critical-session-not-observed" :
            Math.Max(sampleGap, observationGap) > MaximumGap.TotalMilliseconds ? "critical-sample-gap-exceeded" : "accepted";
        return new(reason == "accepted", reason, started, ended, duration, MaximumGap.TotalMilliseconds,
            samples.Length, observations.Length, observations.Length == 0 ? null : observations[0],
            observations.Length == 0 ? null : observations[^1], leading, sampleGap, observationGap, trailing);
    }

    private static double MaxGap(long started, long ended, IReadOnlyList<long> points)
    {
        var previous = started;
        var maximum = 0d;
        foreach (var point in points)
        {
            maximum = Math.Max(maximum, Milliseconds(previous, point));
            previous = point;
        }
        return Math.Max(maximum, Milliseconds(previous, ended));
    }

    private static double Milliseconds(long from, long to) => Stopwatch.GetElapsedTime(from, to).TotalMilliseconds;
}
