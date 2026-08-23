using System.Globalization;
using System.Xml.Linq;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Protocols.MTConnect;

internal static class MtConnectCurrentParser
{
    public static MtConnectCurrentResult ParseResult(
        string xml,
        MachineId machineId,
        string deviceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKey);

        var document = XDocument.Parse(xml, LoadOptions.None);
        var header = document
            .Descendants()
            .SingleOrDefault(element =>
                element.Name.LocalName == "Header");

        if (header is null)
        {
            throw new InvalidDataException(
                "MTConnect current response is missing Header.");
        }

        return new MtConnectCurrentResult
        {
            InstanceId = ParseRequiredSequenceValue(
                header,
                "instanceId"),
            FirstSequence = ParseRequiredSequenceValue(
                header,
                "firstSequence"),
            LastSequence = ParseRequiredSequenceValue(
                header,
                "lastSequence"),
            NextSequence = ParseRequiredSequenceValue(
                header,
                "nextSequence"),
            Observations = ParseObservations(
                document,
                machineId,
                deviceKey),
        };
    }

    public static IReadOnlyList<MachineObservation> ParseObservations(
        string xml,
        MachineId machineId,
        string deviceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKey);

        var document = XDocument.Parse(xml, LoadOptions.None);

        return ParseObservations(
            document,
            machineId,
            deviceKey);
    }

    private static IReadOnlyList<MachineObservation> ParseObservations(
        XDocument document,
        MachineId machineId,
        string deviceKey)
    {
        var deviceStream =
            MtConnectObservationParser.SelectDeviceStream(
                document,
                deviceKey);

        if (deviceStream is null)
        {
            return [];
        }

        return deviceStream
            .Descendants()
            .Where(element =>
                element.Attribute("dataItemId") is not null)
            .Select(element =>
                MtConnectObservationParser.Parse(
                    element,
                    machineId))
            .ToArray();
    }

    private static ulong ParseRequiredSequenceValue(
        XElement element,
        string attributeName)
    {
        var value = (string?)element.Attribute(attributeName);

        if (!ulong.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            throw new InvalidDataException(
                $"MTConnect element '{element.Name.LocalName}' " +
                $"has an invalid or missing '{attributeName}' value.");
        }

        return parsed;
    }
}
