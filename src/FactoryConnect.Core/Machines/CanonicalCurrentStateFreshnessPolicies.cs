using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Machines;

/// <summary>
/// Canonical immutable freshness policies owned by FC-031 composition.
/// </summary>
public static class CanonicalCurrentStateFreshnessPolicies
{
    private static readonly CurrentStatePolicyReference MaximumAgeReference =
        new("freshness/maximum-age", "1.0");

    public static ICurrentStateFreshnessPolicy MaximumAge(
        TimeSpan maximumCurrentAge) =>
        new MaximumAgeCurrentStateFreshnessPolicy(maximumCurrentAge);

    private sealed class MaximumAgeCurrentStateFreshnessPolicy :
        ICurrentStateFreshnessPolicy
    {
        public MaximumAgeCurrentStateFreshnessPolicy(
            TimeSpan maximumCurrentAge)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(
                maximumCurrentAge,
                TimeSpan.Zero);

            MaximumCurrentAge = maximumCurrentAge;
        }

        public CurrentStatePolicyReference Reference => MaximumAgeReference;

        public TimeSpan MaximumCurrentAge { get; }
    }
}
