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

        var xml = await response.Content
            .ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            if (MtConnectErrorParser.TryParse(
                    xml,
                    out var errorResult))
            {
                throw new MtConnectProtocolException(
                    response.StatusCode,
                    errorResult!);
            }

            response.EnsureSuccessStatusCode();
        }

        return MtConnectSampleParser.Parse(
            xml,
            machineId,
            deviceKey);
    }
}
