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
