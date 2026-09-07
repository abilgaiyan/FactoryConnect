using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Persistence.SqlServer;

internal static class OperationalMetricEvaluationKeyV1Codec
{
    public const short CodecVersion = 1;
    public const int MaximumEncodedLength = 6992;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Encode(OperationalMetricEvaluationKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        using var stream = new MemoryStream();
        stream.Write("FCEK"u8);
        stream.WriteByte(0x01);
        WriteUInt16(stream, CodecVersion);
        WriteGuid(stream, key.MachineId.Value);

        switch (key.PeriodId)
        {
            case OperationalMetricPeriodId.Shift shift:
                stream.WriteByte(0x01);
                WriteRequiredString(stream, shift.ShiftOccurrenceId.SiteId.Value);
                WriteRequiredString(stream, shift.ShiftOccurrenceId.ShiftScheduleAssignmentId.Value);
                WriteRequiredString(stream, shift.ShiftOccurrenceId.ShiftId.Value);
                WriteUtcTimestamp(stream, shift.ShiftOccurrenceId.StartsAtUtc);
                WriteUtcTimestamp(stream, shift.ShiftOccurrenceId.EndsAtUtc);
                break;

            case OperationalMetricPeriodId.ProductionDay productionDay:
                stream.WriteByte(0x02);
                WriteRequiredString(stream, productionDay.ProductionDayId.SiteId.Value);
                WriteInt32(stream, productionDay.ProductionDayId.BusinessDate.DayNumber);
                break;

            default:
                throw new InvalidOperationException("Unsupported operational metric period type.");
        }

        WriteOptionalString(stream, key.ContextKey.ProductionOrderId?.Value);
        WriteOptionalString(stream, key.ContextKey.OperationId?.Value);
        WriteOptionalString(stream, key.ContextKey.PartId?.Value);
        WriteOptionalString(stream, key.ContextKey.OperatorId?.Value);
        WriteRequiredString(stream, key.DefinitionId.MetricKey);
        WriteRequiredString(stream, key.DefinitionId.Version);

        if (stream.Length > MaximumEncodedLength)
        {
            throw new ArgumentOutOfRangeException(nameof(key), "Operational metric evaluation key exceeds the frozen V1 maximum encoded length.");
        }

        return stream.ToArray();
    }

    public static byte[] ComputeHash(ReadOnlySpan<byte> encodedKey) => SHA256.HashData(encodedKey);

    private static void WriteRequiredString(Stream stream, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        WriteString(stream, value);
    }

    private static void WriteOptionalString(Stream stream, string? value)
    {
        if (value is null)
        {
            stream.WriteByte(0x00);
            return;
        }

        if (value.Length == 0)
        {
            throw new ArgumentException("Present canonical identity strings must not be empty.", nameof(value));
        }

        stream.WriteByte(0x01);
        WriteString(stream, value);
    }

    private static void WriteString(Stream stream, string value)
    {
        if (value.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "CanonicalStringV1 is limited to 256 UTF-16 code units.");
        }

        byte[] utf8;
        try
        {
            utf8 = StrictUtf8.GetBytes(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("CanonicalStringV1 requires well-formed UTF-16 input.", nameof(value), exception);
        }

        if (utf8.Length > 768)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "CanonicalStringV1 UTF-8 payload is limited to 768 bytes.");
        }

        WriteUInt32(stream, checked((uint)utf8.Length));
        stream.Write(utf8);
    }

    private static void WriteGuid(Stream stream, Guid value)
    {
        Span<byte> buffer = stackalloc byte[16];
        if (!value.TryWriteBytes(buffer, bigEndian: true, out var written) || written != buffer.Length)
        {
            throw new InvalidOperationException("Could not encode Guid using the frozen V1 field order.");
        }

        stream.Write(buffer);
    }

    private static void WriteUtcTimestamp(Stream stream, DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Canonical UTC timestamps require a zero offset.", nameof(value));
        }

        WriteInt64(stream, value.UtcDateTime.Ticks);
    }

    private static void WriteUInt16(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, checked((ushort)value));
        stream.Write(buffer);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        stream.Write(buffer);
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
