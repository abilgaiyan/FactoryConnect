using System.Security.Cryptography;
using System.Text;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed record CanonicalSqlMigration(
    string Text,
    ReadOnlyMemory<byte> Bytes,
    string Sha256Checksum);

internal static class SqlMigrationCanonicalizer
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static CanonicalSqlMigration Canonicalize(ReadOnlySpan<byte> source)
    {
        var offset = HasUtf8Bom(source) ? 3 : 0;
        var text = StrictUtf8.GetString(source[offset..]);
        var normalized = NormalizeNewlines(text);
        var bytes = StrictUtf8.GetBytes(normalized);
        var checksum = Convert.ToHexString(SHA256.HashData(bytes));

        return new CanonicalSqlMigration(normalized, bytes, checksum);
    }

    private static bool HasUtf8Bom(ReadOnlySpan<byte> source) =>
        source.Length >= 3 &&
        source[0] == 0xEF &&
        source[1] == 0xBB &&
        source[2] == 0xBF;

    private static string NormalizeNewlines(string value)
    {
        if (!value.Contains('\r', StringComparison.Ordinal))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (current != '\r')
            {
                builder.Append(current);
                continue;
            }

            if (index + 1 < value.Length && value[index + 1] == '\n')
            {
                index++;
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }
}
