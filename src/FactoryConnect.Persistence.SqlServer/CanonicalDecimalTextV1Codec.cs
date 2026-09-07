using System.Globalization;
using System.Numerics;

namespace FactoryConnect.Persistence.SqlServer;

internal static class CanonicalDecimalTextV1Codec
{
    public static string Serialize(decimal value)
    {
        var bits = decimal.GetBits(value);
        var coefficient =
            ((BigInteger)(uint)bits[2] << 64) |
            ((BigInteger)(uint)bits[1] << 32) |
            (uint)bits[0];
        var scale = (bits[3] >> 16) & 0x7F;
        var negative = (bits[3] & int.MinValue) != 0;

        if (coefficient.IsZero)
        {
            return "0";
        }

        while (scale > 0 && coefficient % 10 == 0)
        {
            coefficient /= 10;
            scale--;
        }

        var digits = coefficient.ToString(CultureInfo.InvariantCulture);
        var exponent = digits.Length - scale - 1;
        var sign = negative ? "-" : string.Empty;

        if (exponent >= -4)
        {
            if (scale == 0)
            {
                return sign + digits;
            }

            var point = digits.Length - scale;
            return point > 0
                ? sign + digits[..point] + "." + digits[point..]
                : sign + "0." + new string('0', -point) + digits;
        }

        var mantissa = digits.Length == 1
            ? digits
            : digits[0] + "." + digits[1..];
        return sign + mantissa + "E-" + (-exponent).ToString("D2", CultureInfo.InvariantCulture);
    }
}
