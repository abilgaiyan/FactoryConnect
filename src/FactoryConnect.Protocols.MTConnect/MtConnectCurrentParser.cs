using System.Globalization;
using System.Xml.Linq;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Protocols.MTConnect;

internal static class MtConnectCurrentParser
{
    private const string Source = "mtconnect";

    public static IReadOnlyList<MachineObservation> Parse(
        string xml,
        MachineId machineId,
        string deviceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKey);

        var document = XDocument.Parse(xml, LoadOptions.None);
        var deviceStreams = document
            .Descendants()
            .Where(element => element.Name.LocalName == "DeviceStream")
            .Where(element => MatchesDevice(element, deviceKey))
            .ToArray();

        if (deviceStreams.Length == 0)
        {
            return [];
        }

        if (deviceStreams.Length > 1)
        {
            throw new InvalidDataException(
                $"Multiple MTConnect DeviceStream elements match device '{deviceKey}'.");
        }

        return deviceStreams[0]
            .Descendants()
            .Where(element => element.Attribute("dataItemId") is not null)
            .Select(element => ToObservation(element, machineId))
            .ToArray();
    }

    private static bool MatchesDevice(XElement deviceStream, string deviceKey)
    {
        var name = (string?)deviceStream.Attribute("name");
        var uuid = (string?)deviceStream.Attribute("uuid");

        return string.Equals(name, deviceKey, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(uuid, deviceKey, StringComparison.OrdinalIgnoreCase);
    }

    private static MachineObservation ToObservation(
        XElement element,
        MachineId machineId)
    {
        var dataItemId = (string?)element.Attribute("dataItemId");
        if (string.IsNullOrWhiteSpace(dataItemId))
        {
            throw new InvalidDataException(
                "MTConnect current observation is missing dataItemId.");
        }

        var timestampText = (string?)element.Attribute("timestamp");
        if (!DateTimeOffset.TryParse(
                timestampText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var timestamp))
        {
            throw new InvalidDataException(
                $"MTConnect observation '{dataItemId}' has an invalid timestamp.");
        }

        var type = ResolveSignalType(element);
        var rawValue = element.Value.Trim();
        var unavailable = string.Equals(
            rawValue,
            "UNAVAILABLE",
            StringComparison.OrdinalIgnoreCase);

        return new MachineObservation
        {
            MachineId = machineId,
            Source = Source,
            Address = dataItemId,
            Type = type,
            Value = unavailable ? null : ConvertValue(rawValue, type, dataItemId),
            Quality = unavailable ? ObservationQuality.Uncertain : ObservationQuality.Good,
            Timestamp = timestamp,
        };
    }

    private static SignalType ResolveSignalType(XElement element)
    {
        var category = element
            .Ancestors()
            .Select(ancestor => ancestor.Name.LocalName)
            .FirstOrDefault(name =>
                name is "Samples" or "Events" or "Condition");

        return category switch
        {
            "Samples" => SignalType.Numeric,
            "Events" => SignalType.Enumeration,
            "Condition" => SignalType.Text,
            _ => throw new InvalidDataException(
                $"MTConnect observation '{element.Name.LocalName}' is not inside Samples, Events, or Condition."),
        };
    }

    private static object ConvertValue(
        string rawValue,
        SignalType type,
        string dataItemId)
    {
        if (type != SignalType.Numeric)
        {
            return rawValue;
        }

        if (decimal.TryParse(
                rawValue,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var numericValue))
        {
            return numericValue;
        }

        throw new InvalidDataException(
            $"MTConnect sample '{dataItemId}' contains non-numeric value '{rawValue}'.");
    }
}
