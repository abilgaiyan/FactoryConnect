using System.Buffers.Binary;
using System.Security.Cryptography;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlServerShiftOccurrenceIdentityCodec
{
    public static byte[] Encode(ShiftOccurrenceId occurrence)
    {
        ArgumentNullException.ThrowIfNull(occurrence);

        using var stream = new MemoryStream();
        WriteString(stream, occurrence.SiteId.Value);
        WriteString(stream, occurrence.ShiftScheduleAssignmentId.Value);
        WriteString(stream, occurrence.ShiftId.Value);
        WriteInt64(stream, occurrence.StartsAtUtc.UtcTicks);
        WriteInt64(stream, occurrence.EndsAtUtc.UtcTicks);
        return stream.ToArray();
    }

    public static byte[] Hash(byte[] canonicalIdentity)
    {
        ArgumentNullException.ThrowIfNull(canonicalIdentity);
        return SHA256.HashData(canonicalIdentity);
    }

    private static void WriteString(Stream stream, string value)
    {
        var encoded = OrdinalStringKeyCodec.Encode(value);
        WriteInt32(stream, encoded.Length);
        stream.Write(encoded);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buffer, value);
        stream.Write(buffer);
    }
}
