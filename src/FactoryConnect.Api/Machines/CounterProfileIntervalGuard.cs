using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Api.Machines;

/// <summary>Diagnostic profile selection only; never qualifies production evidence.</summary>
public static class CounterProfileIntervalGuard
{
    public static MachineProductionEvidenceProfile? EffectiveUnchanged(
        IReadOnlyList<MachineProductionEvidenceProfile> profiles,
        DateTimeOffset fromInclusive, DateTimeOffset toExclusive)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        if (fromInclusive.Offset != TimeSpan.Zero || toExclusive.Offset != TimeSpan.Zero ||
            toExclusive <= fromInclusive)
            throw new ArgumentException("Positive UTC interval required.");
        var effective = profiles
            .Where(p => p.EffectiveFromUtc <= fromInclusive)
            .OrderByDescending(p => p.EffectiveFromUtc)
            .FirstOrDefault();
        if (effective is null) return null;
        // A revision effective at the exclusive end does not affect this interval.
        if (profiles.Any(p => p.EffectiveFromUtc > fromInclusive &&
                              p.EffectiveFromUtc < toExclusive))
            return null;
        return effective;
    }
}
