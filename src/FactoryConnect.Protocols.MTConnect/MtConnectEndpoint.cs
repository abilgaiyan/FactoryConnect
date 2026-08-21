namespace FactoryConnect.Protocols.MTConnect;

public sealed record MtConnectEndpoint
{
    public Uri BaseUri { get; }

    public Uri ProbeUri => new(BaseUri, "probe");

    public MtConnectEndpoint(Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);

        if (!baseUri.IsAbsoluteUri)
        {
            throw new ArgumentException(
                "MTConnect base URI must be absolute.",
                nameof(baseUri));
        }

        if (!string.Equals(baseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "MTConnect base URI must use HTTP or HTTPS.",
                nameof(baseUri));
        }

        var normalized = baseUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? baseUri.AbsoluteUri
            : $"{baseUri.AbsoluteUri}/";

        BaseUri = new Uri(normalized, UriKind.Absolute);
    }
}
