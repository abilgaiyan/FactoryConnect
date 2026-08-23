using FactoryConnect.Abstractions;

namespace FactoryConnect.Protocols.MTConnect;

public sealed class MtConnectAcquisitionSession
{
    private readonly MtConnectSampleClient _client;

    private ulong? _instanceId;
    private ulong _nextSequence;

    public MtConnectAcquisitionSession(
        MtConnectSampleClient client,
        ulong fromSequence)
    {
        ArgumentNullException.ThrowIfNull(client);

        _client = client;
        _nextSequence = fromSequence;
    }

    public ulong? InstanceId => _instanceId;

    public ulong NextSequence => _nextSequence;

    public async Task<MtConnectSampleResult> AcquireNextAsync(
        MtConnectEndpoint endpoint,
        MachineId machineId,
        string deviceKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKey);

        var result = await _client.AcquireAsync(
            endpoint,
            machineId,
            deviceKey,
            _nextSequence,
            cancellationToken);

        if (_instanceId is not null &&
            _instanceId.Value != result.InstanceId)
        {
            throw new MtConnectInstanceChangedException(
                _instanceId.Value,
                result.InstanceId,
                result.FirstSequence);
        }

        _instanceId ??= result.InstanceId;
        _nextSequence = result.NextSequence;

        return result;
    }
}
