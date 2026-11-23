using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Machines;

public enum CounterIdentityDisposition
{
    NoConfiguredDataItem,
    NoDiscoveredSignals,
    NoExactAddressCandidate,
    AmbiguousExactAddressCandidates,
    SingleUnverifiedCandidate
}

/// <summary>
/// A read-only raw signal identity diagnostic. Exact address equality is not
/// proof of MTConnect data-item identity, acquisition ownership, or quantity admission.
/// </summary>
public sealed record CounterIdentityAssessment(
    CounterIdentityDisposition Disposition,
    IReadOnlyList<CounterSignal> Candidates,
    bool IdentityVerified,
    bool ProductionQuantityAdmitted);

public static class CounterIdentityAssessor
{
    /// <summary>
    /// Discover literal candidate addresses from a validated, pinned durable raw prefix.
    /// This does not establish data-item ownership or continuity.
    /// </summary>
    public static CounterIdentityAssessment AssessDurablePrefix(
        string? configuredCounterDataItemId,
        ObservationStreamId stream,
        ObservationPosition through,
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive,
        IReadOnlyList<DurableMachineObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(through);
        ArgumentNullException.ThrowIfNull(observations);
        if (fromInclusive.Offset != TimeSpan.Zero || toExclusive.Offset != TimeSpan.Zero ||
            toExclusive <= fromInclusive)
            throw new ArgumentException("Positive UTC observation interval required.");

        ObservationPosition? previous = null;
        var signals = new HashSet<CounterSignal>();
        foreach (var item in observations)
        {
            if (item.StreamId != stream || item.Position <= previous || item.Position > through)
                throw new InvalidDataException("Raw counter prefix identity or ordering mismatch.");
            previous = item.Position;
            var raw = item.Observation;
            if (!Enum.IsDefined(raw.Type) || !Enum.IsDefined(raw.Quality))
                throw new InvalidDataException("Unsupported raw observation classification.");
            if (raw.Timestamp >= fromInclusive && raw.Timestamp < toExclusive &&
                raw.Type is SignalType.Numeric or SignalType.Enumeration)
                signals.Add(new CounterSignal(raw.Source, raw.Address));
        }

        return Assess(configuredCounterDataItemId,
            signals.OrderBy(x => x.Source, StringComparer.Ordinal)
                .ThenBy(x => x.Address, StringComparer.Ordinal).ToArray());
    }

    public static CounterIdentityAssessment Assess(
        string? configuredCounterDataItemId,
        IReadOnlyList<CounterSignal> availableSignals)
    {
        ArgumentNullException.ThrowIfNull(availableSignals);
        if (configuredCounterDataItemId is null)
            return Result(CounterIdentityDisposition.NoConfiguredDataItem, []);
        if (string.IsNullOrWhiteSpace(configuredCounterDataItemId))
            throw new ArgumentException("Configured data item ID cannot be blank.", nameof(configuredCounterDataItemId));
        if (availableSignals.Count == 0)
            return Result(CounterIdentityDisposition.NoDiscoveredSignals, []);

        var candidates = availableSignals
            .Where(signal => signal is not null &&
                string.Equals(signal.Address, configuredCounterDataItemId, StringComparison.Ordinal))
            .Distinct()
            .OrderBy(signal => signal.Source, StringComparer.Ordinal)
            .ThenBy(signal => signal.Address, StringComparer.Ordinal)
            .ToArray();

        return Result(candidates.Length switch
        {
            0 => CounterIdentityDisposition.NoExactAddressCandidate,
            1 => CounterIdentityDisposition.SingleUnverifiedCandidate,
            _ => CounterIdentityDisposition.AmbiguousExactAddressCandidates
        }, candidates);
    }

    private static CounterIdentityAssessment Result(
        CounterIdentityDisposition disposition, IReadOnlyList<CounterSignal> candidates) =>
        new(disposition, Array.AsReadOnly(candidates.ToArray()),
            IdentityVerified: false, ProductionQuantityAdmitted: false);
}
