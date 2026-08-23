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
        var xml = await AcquireDocumentAsync(
            endpoint,
            deviceKey,
            cancellationToken);

        return MtConnectCurrentParser.ParseObservations(
            xml,
            machineId,
            deviceKey);
    }

    public async Task<MtConnectCurrentResult> AcquireResultAsync(
        MtConnectEndpoint endpoint,
        MachineId machineId,
        string deviceKey,
        CancellationToken cancellationToken = default)
    {
        var xml = await AcquireDocumentAsync(
            endpoint,
            deviceKey,
            cancellationToken);

        return MtConnectCurrentParser.ParseResult(
            xml,
            machineId,
            deviceKey);
    }

    private async Task<string> AcquireDocumentAsync(
        MtConnectEndpoint endpoint,
        string deviceKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKey);

        using var response = await _httpClient.GetAsync(
            endpoint.CurrentUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        return await response.Content
            .ReadAsStringAsync(cancellationToken);
    }
}
