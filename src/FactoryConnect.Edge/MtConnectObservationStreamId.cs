using FactoryConnect.Abstractions;

namespace FactoryConnect.Edge;

public static class MtConnectObservationStreamId
{
    public static ObservationStreamId Create(
        MachineId machineId,
        string deviceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKey);

        return new ObservationStreamId(
            machineId,
            $"mtconnect:{deviceKey.Trim().ToUpperInvariant()}");
    }
}
