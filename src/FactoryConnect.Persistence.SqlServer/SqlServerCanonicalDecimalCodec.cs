using System.Globalization;

namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlServerCanonicalDecimalCodec
{
    private const string DecimalFormat = "G29";

    public static string Serialize(decimal value) =>
        value.ToString(DecimalFormat, CultureInfo.InvariantCulture);

    public static decimal Deserialize(string persistedValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(persistedValue);

        if (decimal.TryParse(
                persistedValue,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value))
        {
            return value;
        }

        throw new InvalidDataException(
            $"Persisted SQL Server decimal value '{persistedValue}' is invalid.");
    }
}
