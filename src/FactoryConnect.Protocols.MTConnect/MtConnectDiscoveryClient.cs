namespace FactoryConnect.Protocols.MTConnect;

public sealed class MtConnectDiscoveryClient
{
    private readonly HttpClient _httpClient;

    public MtConnectDiscoveryClient(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    public async Task<MtConnectDiscoveryResult> DiscoverAsync(
        MtConnectEndpoint endpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        using var response = await _httpClient.GetAsync(
            endpoint.ProbeUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var xml = await response.Content.ReadAsStringAsync(cancellationToken);
        return MtConnectProbeParser.Parse(xml);
    }
}
