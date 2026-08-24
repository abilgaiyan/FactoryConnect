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
        : this(client, null, fromSequence)
    {
    }

    public MtConnectAcquisitionSession(
        MtConnectSampleClient client,
        ulong instanceId,
        ulong nextSequence)
        : this(client, (ulong?)instanceId, nextSequence)
    {
    }

    private MtConnectAcquisitionSession(
        MtConnectSampleClient client,
        ulong? instanceId,
        ulong nextSequence)
    {
        ArgumentNullException.ThrowIfNull(client);

        _client = client;
        _instanceId = instanceId;
        _nextSequence = nextSequence;
    }

    public ulong? InstanceId => _instanceId;

    public ulong NextSequence => _nextSequence;

    public async Task<MtConnectSampleResult> AcquireNextAsync(
        MtConnectEndpoint endpoint,
        MachineId machineId,
        string deviceKey,
        CancellationToken cancellationToken = default)
    {
        var result = await PrepareNextAsync(
            endpoint,
            machineId,
            deviceKey,
            cancellationToken);

        Advance(result);

        return result;
    }

    public async Task<MtConnectSampleResult> PrepareNextAsync(
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

        ValidateInstance(result);

        return result;
    }

    public void Advance(MtConnectSampleResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ValidateInstance(result);

        _instanceId ??= result.InstanceId;
        _nextSequence = result.NextSequence;
    }

    private void ValidateInstance(MtConnectSampleResult result)
    {
        if (_instanceId is not null &&
            _instanceId.Value != result.InstanceId)
        {
            throw new MtConnectInstanceChangedException(
                _instanceId.Value,
                result.InstanceId,
                result.FirstSequence);
        }
    }
}
