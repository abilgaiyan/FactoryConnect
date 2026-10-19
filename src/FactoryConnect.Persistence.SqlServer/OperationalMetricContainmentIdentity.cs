using System.Security.Cryptography;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>
/// RC2 content digests. These identify bytes only; callers must separately validate
/// the canonical envelope, semantic binding and historical source authority.
/// </summary>
internal static class OperationalMetricContainmentIdentity
{
    internal const int MaximumWitnessLength = 65_536;
    internal const int MaximumResultLength = 16_777_216;

    public static byte[] ComputeWitnessId(ReadOnlySpan<byte> envelope) =>
        Compute("FactoryConnect/AvailabilityScopeConflictEvidence/v1\0"u8, envelope, MaximumWitnessLength);

    public static byte[] ComputeResultId(ReadOnlySpan<byte> envelope) =>
        Compute("FactoryConnect/OperationalMetricPublicationResult/v2\0"u8, envelope, MaximumResultLength);

    public static byte[] ComputeDependencyHash(ReadOnlySpan<byte> envelope) =>
        Compute("FactoryConnect/OperationalMetricDependencySnapshot/v2\0"u8, envelope, MaximumResultLength);

    private static byte[] Compute(ReadOnlySpan<byte> domain, ReadOnlySpan<byte> envelope, int maximumLength)
    {
        if (envelope.IsEmpty || envelope.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(nameof(envelope), "Containment envelope exceeds its encoded length bounds.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(domain);
        hash.AppendData(envelope);
        return hash.GetHashAndReset();
    }
}
