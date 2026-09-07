namespace FactoryConnect.Persistence.SqlServer;

internal static class StringOrderKeyV2Codec
{
    internal const int MaximumCodeUnits = 256;
    internal const int MaximumEncodedLength = (MaximumCodeUnits * 3) + 1;

    public static byte[] Encode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length > MaximumCodeUnits)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"StringOrderKeyV2 supports at most {MaximumCodeUnits} UTF-16 code units.");
        }

        var encoded = new byte[(value.Length * 3) + 1];
        var offset = 0;

        foreach (var codeUnit in value)
        {
            encoded[offset++] = 0x01;
            encoded[offset++] = (byte)(codeUnit >> 8);
            encoded[offset++] = (byte)codeUnit;
        }

        encoded[offset] = 0x00;
        return encoded;
    }
}
