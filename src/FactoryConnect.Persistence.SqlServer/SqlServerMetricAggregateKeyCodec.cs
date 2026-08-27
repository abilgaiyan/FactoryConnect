using System.Buffers.Binary;
using System.Security.Cryptography;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlServerMetricAggregateKeyCodec
{
    public static byte[] Encode(ShiftMetricAggregateKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        using var stream = new MemoryStream();
        WriteGuid(stream, key.MachineId.Value);
        WriteString(stream, key.ShiftOccurrenceId.SiteId.Value);
        WriteString(stream, key.ShiftOccurrenceId.ShiftScheduleAssignmentId.Value);
        WriteString(stream, key.ShiftOccurrenceId.ShiftId.Value);
        WriteInt64(stream, key.ShiftOccurrenceId.StartsAtUtc.UtcTicks);
        WriteInt64(stream, key.ShiftOccurrenceId.EndsAtUtc.UtcTicks);
        WriteString(stream, key.MetricInputKey);
        return stream.ToArray();
    }

    public static byte[] Encode(ProductionDayMetricAggregateKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        using var stream = new MemoryStream();
        WriteGuid(stream, key.MachineId.Value);
        WriteString(stream, key.ProductionDayId.SiteId.Value);
        WriteInt32(stream, key.ProductionDayId.BusinessDate.DayNumber);
        WriteString(stream, key.MetricInputKey);
        return stream.ToArray();
    }

    public static byte[] Hash(byte[] canonicalKey)
    {
        ArgumentNullException.ThrowIfNull(canonicalKey);
        return SHA256.HashData(canonicalKey);
    }

    private static void WriteGuid(Stream stream, Guid value)
    {
        Span<byte> buffer = stackalloc byte[16];
        if (!value.TryWriteBytes(buffer, bigEndian: true, out var bytesWritten) ||
            bytesWritten != buffer.Length)
        {
            throw new InvalidOperationException("Unable to encode aggregate machine identity.");
        }

        stream.Write(buffer);
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
