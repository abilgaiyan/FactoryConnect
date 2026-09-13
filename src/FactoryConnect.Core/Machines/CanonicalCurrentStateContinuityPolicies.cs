using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Machines;

/// <summary>
/// Canonical immutable continuity policies owned by FC-031 composition.
/// Registration and production cutover are deferred to FC-031.2D.5B.
/// </summary>
public static class CanonicalCurrentStateContinuityPolicies
{
    public static CurrentStateContinuityPolicy Preserve { get; } =
        new(
            new CurrentStatePolicyReference("continuity/preserve", "1.0"),
            StateContinuityMode.Preserve);
}
