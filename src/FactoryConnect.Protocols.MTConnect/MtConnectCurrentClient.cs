using FactoryConnect.Abstractions;

namespace FactoryConnect.Protocols.MTConnect;

public sealed class MtConnectCurrentClient
{
    private readonly HttpClient _httpClient;

    public MtConnectCurrentClient(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<MachineObservation>> AcquireAsync(
        MtConnectEndpoint endpoint,
        MachineId machineId,
        string deviceKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKey);

        using var response = await _httpClient.GetAsync(
            endpoint.CurrentUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var xml = await response.Content.ReadAsStringAsync(cancellationToken);
        return MtConnectCurrentParser.Parse(xml, machineId, deviceKey);
    }
}
