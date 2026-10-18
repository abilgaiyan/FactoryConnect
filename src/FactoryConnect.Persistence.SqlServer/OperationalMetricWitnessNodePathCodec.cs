using System.Buffers.Binary;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>
/// RC2 archive reference path: big-endian U32 count, then dependency ordinals.
/// Decoding establishes shape only. The result validator must resolve every
/// ordinal against the decoded dependency tree and compare the complete reference set.
/// </summary>
internal static class OperationalMetricWitnessNodePathCodec
{
    internal const int MaximumOrdinalCount = 63;

    public static byte[] Encode(ReadOnlySpan<uint> dependencyOrdinals)
    {
        if (dependencyOrdinals.Length > MaximumOrdinalCount)
        {
            throw new ArgumentOutOfRangeException(nameof(dependencyOrdinals), "Witness path exceeds the maximum dependency depth.");
        }

        var encoded = new byte[(dependencyOrdinals.Length + 1) * sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(encoded, (uint)dependencyOrdinals.Length);
        for (var index = 0; index < dependencyOrdinals.Length; index++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(encoded.AsSpan((index + 1) * sizeof(uint)), dependencyOrdinals[index]);
        }

        return encoded;
    }

    public static uint[] Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length < sizeof(uint) || encoded.Length > (MaximumOrdinalCount + 1) * sizeof(uint))
        {
            throw new InvalidDataException("Witness path exceeds its encoded length bounds.");
        }

        var count = BinaryPrimitives.ReadUInt32BigEndian(encoded);
        // Bound count before arithmetic or allocation, including forged UInt32.MaxValue.
        if (count > MaximumOrdinalCount || encoded.Length != ((int)count + 1) * sizeof(uint))
        {
            throw new InvalidDataException("Witness path count does not match its encoded length.");
        }

        var ordinals = new uint[(int)count];
        for (var index = 0; index < ordinals.Length; index++)
        {
            ordinals[index] = BinaryPrimitives.ReadUInt32BigEndian(encoded[((index + 1) * sizeof(uint))..]);
        }

        return ordinals;
    }
}
