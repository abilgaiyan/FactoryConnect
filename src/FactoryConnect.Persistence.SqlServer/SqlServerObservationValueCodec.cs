using System.Globalization;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlServerObservationValueCodec
{
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

    public static bool AreEquivalent(
        SignalType type,
        object? left,
        object? right)
    {
        return type switch
        {
            SignalType.Numeric => NumericEquivalent(left, right),
            SignalType.Enumeration => StringEquivalent(type, left, right),
            SignalType.Text => StringEquivalent(type, left, right),
            _ => throw Unsupported(type, left),
        };
    }

    private static string? SerializeNumeric(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is decimal numeric)
        {
            return numeric.ToString(CultureInfo.InvariantCulture);
        }

        throw Unsupported(SignalType.Numeric, value);
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

    private static bool NumericEquivalent(
        object? left,
        object? right)
    {
        ValidateNumeric(left);
        ValidateNumeric(right);

        return left switch
        {
            null => right is null,
            decimal leftValue when right is decimal rightValue =>
                leftValue == rightValue,
            _ => false,
        };
    }

    private static bool StringEquivalent(
        SignalType type,
        object? left,
        object? right)
    {
        ValidateString(type, left);
        ValidateString(type, right);

        return left switch
        {
            null => right is null,
            string leftValue when right is string rightValue =>
                string.Equals(
                    leftValue,
                    rightValue,
                    StringComparison.Ordinal),
            _ => false,
        };
    }

    private static void ValidateNumeric(object? value)
    {
        if (value is not null && value is not decimal)
        {
            throw Unsupported(SignalType.Numeric, value);
        }
    }

    private static void ValidateString(
        SignalType type,
        object? value)
    {
        if (value is not null && value is not string)
        {
            throw Unsupported(type, value);
        }
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
