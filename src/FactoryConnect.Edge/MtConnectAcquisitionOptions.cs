using FactoryConnect.Abstractions;
using FactoryConnect.Protocols.MTConnect;

namespace FactoryConnect.Edge;

public sealed record MtConnectAcquisitionOptions
{
    public MtConnectEndpoint Endpoint { get; }

    public MachineId MachineId { get; }

    public string DeviceKey { get; }

    public ulong FromSequence { get; }

    public TimeSpan PollingInterval { get; }

    public MtConnectAcquisitionOptions(
        MtConnectEndpoint endpoint,
        MachineId machineId,
        string deviceKey,
        ulong fromSequence,
        TimeSpan pollingInterval)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKey);

        if (machineId.IsEmpty)
        {
            throw new ArgumentException(
                "Machine identifier must not be empty.",
                nameof(machineId));
        }

        if (pollingInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pollingInterval),
                pollingInterval,
                "Polling interval must be greater than zero.");
        }

        Endpoint = endpoint;
        MachineId = machineId;
        DeviceKey = deviceKey;
        FromSequence = fromSequence;
        PollingInterval = pollingInterval;
    }
}
