using System.Xml.Linq;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Protocols.MTConnect;

internal static class MtConnectCurrentParser
{
    public static IReadOnlyList<MachineObservation> Parse(
        string xml,
        MachineId machineId,
        string deviceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKey);

        var document = XDocument.Parse(xml, LoadOptions.None);

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
}
