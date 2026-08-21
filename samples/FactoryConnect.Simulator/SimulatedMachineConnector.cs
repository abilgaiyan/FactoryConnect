using FactoryConnect.Abstractions;

namespace FactoryConnect.Simulator;

public sealed class SimulatedMachineConnector : IMachineConnector
{
    private int _readCount;

    public SimulatedMachineConnector(MachineId machineId)
    {
        MachineId = machineId;
    }

    public MachineId MachineId { get; }

    public ValueTask<MachineSignalSnapshot> ReadSignalsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cycle = Interlocked.Increment(ref _readCount);
        var fault = cycle % 10 == 0;
        var running = cycle % 4 is 1 or 2;
        var idle = !fault && !running && cycle % 4 == 3;
        var cycleCount = cycle / 4;

        var signals = new[]
        {
            new MachineSignalValue
            {
                Key = CanonicalSignalKeys.Running,
                Type = SignalType.Digital,
                Value = running && !fault,
                Source = "simulator"
            },
            new MachineSignalValue
            {
                Key = CanonicalSignalKeys.Idle,
                Type = SignalType.Digital,
                Value = idle,
                Source = "simulator"
            },
            new MachineSignalValue
            {
                Key = CanonicalSignalKeys.Fault,
                Type = SignalType.Digital,
                Value = fault,
                Source = "simulator"
            },
            new MachineSignalValue
            {
                Key = CanonicalSignalKeys.CycleCount,
                Type = SignalType.Counter,
                Value = cycleCount,
                Source = "simulator"
            }
        };

        return ValueTask.FromResult(
            new MachineSignalSnapshot(MachineId, signals, DateTimeOffset.UtcNow));
    }
}
