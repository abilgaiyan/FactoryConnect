using System.Globalization;
using System.Xml.Linq;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Protocols.MTConnect;

internal static class MtConnectSampleParser
{
    public static MtConnectSampleResult Parse(
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
                "MTConnect sample response is missing Header.");
        }

        var instanceId =
            ParseRequiredSequenceValue(header, "instanceId");

        var firstSequence =
            ParseRequiredSequenceValue(header, "firstSequence");

        var lastSequence =
            ParseRequiredSequenceValue(header, "lastSequence");

        var nextSequence =
            ParseRequiredSequenceValue(header, "nextSequence");

        var deviceStream =
            MtConnectObservationParser.SelectDeviceStream(
                document,
                deviceKey);

        if (deviceStream is null)
        {
            return new MtConnectSampleResult
            {
                InstanceId = instanceId,
                FirstSequence = firstSequence,
                LastSequence = lastSequence,
                NextSequence = nextSequence,
                Observations = [],
            };
        }

        var observations = deviceStream
            .Descendants()
            .Where(element =>
                element.Attribute("dataItemId") is not null)
            .Select(element =>
                ParseObservation(element, machineId))
            .OrderBy(observation =>
                observation.Sequence)
            .ToArray();

        return new MtConnectSampleResult
        {
            InstanceId = instanceId,
            FirstSequence = firstSequence,
            LastSequence = lastSequence,
            NextSequence = nextSequence,
            Observations = observations,
        };
    }

    private static MtConnectSampleObservation ParseObservation(
        XElement element,
        MachineId machineId)
    {
        var sequence = ParseRequiredSequenceValue(
            element,
            "sequence");

        return new MtConnectSampleObservation
        {
            Sequence = sequence,
            Observation =
                MtConnectObservationParser.Parse(
                    element,
                    machineId),
        };
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
