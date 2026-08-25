using System.Buffers.Binary;

namespace FactoryConnect.Persistence.SqlServer;

internal static class OrdinalStringKeyCodec
{
    public const int MaxCodeUnits = 256;

    public static byte[] Encode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length > MaxCodeUnits)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value.Length,
                $"Stream keys must not exceed {MaxCodeUnits} UTF-16 code units.");
        }

        var result = new byte[checked(value.Length * 2)];

        for (var index = 0; index < value.Length; index++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(
                result.AsSpan(index * 2, 2),
                value[index]);
        }

        return result;
    }

    public static string Decode(ReadOnlySpan<byte> value)
    {
        if ((value.Length & 1) != 0)
        {
            throw new ArgumentException(
                "Ordinal stream-key bytes must contain complete UTF-16 code units.",
                nameof(value));
        }

        if (value.Length > MaxCodeUnits * 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value.Length,
                $"Ordinal stream-key bytes must not exceed {MaxCodeUnits * 2} bytes.");
        }

        var chars = new char[value.Length / 2];

        for (var index = 0; index < chars.Length; index++)
        {
            chars[index] = (char)BinaryPrimitives.ReadUInt16BigEndian(
                value.Slice(index * 2, 2));
        }

        return new string(chars);
    }
}
