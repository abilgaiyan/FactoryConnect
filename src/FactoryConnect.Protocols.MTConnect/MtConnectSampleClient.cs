using FactoryConnect.Abstractions;

namespace FactoryConnect.Protocols.MTConnect;

public sealed class MtConnectSampleClient
{
    private readonly HttpClient _httpClient;

    public MtConnectSampleClient(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        _httpClient = httpClient;
    }

    public async Task<MtConnectSampleResult> AcquireAsync(
        MtConnectEndpoint endpoint,
        MachineId machineId,
        string deviceKey,
        ulong fromSequence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKey);

        using var response = await _httpClient.GetAsync(
            endpoint.SampleUri(fromSequence),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var xml = await response.Content
            .ReadAsStringAsync(cancellationToken);

        return MtConnectSampleParser.Parse(
            xml,
            machineId,
            deviceKey);
    }
}
