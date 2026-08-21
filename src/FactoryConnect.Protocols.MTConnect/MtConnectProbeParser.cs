using System.Xml.Linq;

namespace FactoryConnect.Protocols.MTConnect;

internal static class MtConnectProbeParser
{
    public static MtConnectDiscoveryResult Parse(string xml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);

        var document = XDocument.Parse(xml, LoadOptions.None);
        var header = document
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "Header");

        var devices = document
            .Descendants()
            .Where(element => element.Name.LocalName == "Device")
            .Select(ParseDevice)
            .ToArray();

        return new MtConnectDiscoveryResult
        {
            AgentInstanceId = Attribute(header, "instanceId"),
            AgentVersion = Attribute(header, "version"),
            Devices = devices,
        };
    }

    private static MtConnectDeviceDescriptor ParseDevice(XElement device)
    {
        var id = RequiredAttribute(device, "id", "Device");

        var dataItems = device
            .Descendants()
            .Where(element => element.Name.LocalName == "DataItem")
            .Select(ParseDataItem)
            .ToArray();

        return new MtConnectDeviceDescriptor
        {
            Id = id,
            Name = Attribute(device, "name"),
            Uuid = Attribute(device, "uuid"),
            DataItems = dataItems,
        };
    }

    private static MtConnectDataItemDescriptor ParseDataItem(XElement dataItem)
    {
        var component = dataItem.Parent?.Parent;
        if (component?.Name.LocalName == "Device")
        {
            component = null;
        }

        return new MtConnectDataItemDescriptor
        {
            Id = RequiredAttribute(dataItem, "id", "DataItem"),
            Name = Attribute(dataItem, "name"),
            Type = RequiredAttribute(dataItem, "type", "DataItem"),
            Category = Attribute(dataItem, "category"),
            SubType = Attribute(dataItem, "subType"),
            Units = Attribute(dataItem, "units"),
            ComponentId = Attribute(component, "id"),
            ComponentName = Attribute(component, "name"),
            ComponentType = component?.Name.LocalName,
        };
    }

    private static string RequiredAttribute(
        XElement element,
        string name,
        string elementName) =>
        Attribute(element, name) ??
        throw new InvalidDataException(
            $"MTConnect {elementName} is missing required attribute '{name}'.");

    private static string? Attribute(XElement? element, string name) =>
        element?.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == name)
            ?.Value;
}
