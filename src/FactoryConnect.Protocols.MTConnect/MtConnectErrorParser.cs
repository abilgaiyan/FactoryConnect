using System.Globalization;
using System.Xml.Linq;

namespace FactoryConnect.Protocols.MTConnect;

internal static class MtConnectErrorParser
{
    public static bool TryParse(
        string xml,
        out MtConnectErrorResult? result)
    {
        result = null;

        if (string.IsNullOrWhiteSpace(xml))
        {
            return false;
        }

        XDocument document;

        try
        {
            document = XDocument.Parse(xml, LoadOptions.None);
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }

        var root = document.Root;

        if (root is null ||
            root.Name.LocalName != "MTConnectError")
        {
            return false;
        }

        ulong? instanceId = null;

        var header = document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Header");

        var instanceIdText =
            (string?)header?.Attribute("instanceId");

        if (!string.IsNullOrWhiteSpace(instanceIdText))
        {
            if (!ulong.TryParse(
                    instanceIdText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsedInstanceId))
            {
                throw new InvalidDataException(
                    "MTConnect error response has an invalid instanceId.");
            }

            instanceId = parsedInstanceId;
        }

        var errors = document
            .Descendants()
            .Where(element =>
                element.Name.LocalName == "Error")
            .Select(element =>
            {
                var code =
                    (string?)element.Attribute("errorCode");

                if (string.IsNullOrWhiteSpace(code))
                {
                    throw new InvalidDataException(
                        "MTConnect error is missing errorCode.");
                }

                return new MtConnectError
                {
                    Code = code,
                    Message = element.Value.Trim(),
                };
            })
            .ToArray();

        if (errors.Length == 0)
        {
            throw new InvalidDataException(
                "MTConnect error response contains no Error elements.");
        }

        result = new MtConnectErrorResult
        {
            InstanceId = instanceId,
            Errors = errors,
        };

        return true;
    }
}
