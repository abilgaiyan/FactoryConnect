using System.Globalization;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlServerObservationValueCodec
{
    private const string DecimalFormat = "G29";

    public static string? Serialize(
        SignalType type,
        object? value)
    {
        return type switch
        {
            SignalType.Numeric => SerializeNumeric(value),
            SignalType.Enumeration => SerializeString(type, value),
            SignalType.Text => SerializeString(type, value),
            _ => throw Unsupported(type, value),
        };
    }

    public static object? Deserialize(
        SignalType type,
        string? persistedValue)
    {
        return type switch
        {
            SignalType.Numeric => persistedValue is null
                ? null
                : DeserializeNumeric(persistedValue),
            SignalType.Enumeration => persistedValue,
            SignalType.Text => persistedValue,
            _ => throw Unsupported(type, persistedValue),
        };
    }

    public static bool AreEquivalent(
        SignalType type,
        object? left,
        object? right) =>
        string.Equals(
            Serialize(type, left),
            Serialize(type, right),
            StringComparison.Ordinal);

    private static string? SerializeNumeric(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is decimal numeric)
        {
            return numeric.ToString(
                DecimalFormat,
                CultureInfo.InvariantCulture);
        }

        throw Unsupported(SignalType.Numeric, value);
    }

    private static decimal DeserializeNumeric(string persistedValue)
    {
        if (decimal.TryParse(
                persistedValue,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var numeric))
        {
            return numeric;
        }

        throw new InvalidDataException(
            $"Persisted SQL Server numeric observation value " +
            $"'{persistedValue}' is invalid.");
    }

    private static string? SerializeString(
        SignalType type,
        object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is string text)
        {
            return text;
        }

        throw Unsupported(type, value);
    }

    private static InvalidOperationException Unsupported(
        SignalType type,
        object? value)
    {
        var valueType = value?.GetType().FullName ?? "null";

        return new InvalidOperationException(
            $"Signal type '{type}' does not support CLR value type " +
            $"'{valueType}' in the SQL Server persistence provider.");
    }
}
