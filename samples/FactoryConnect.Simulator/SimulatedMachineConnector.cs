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
        var running = cycle % 4 is 1 or 2;
        var fault = cycle % 10 == 0;
        var cyclePulse = cycle % 4 == 2;

        var signals = new[]
        {
            new MachineSignalValue
            {
                Key = "Running",
                Type = SignalType.Digital,
                Value = running && !fault
            },
            new MachineSignalValue
            {
                Key = "Fault",
                Type = SignalType.Digital,
                Value = fault
            },
            new MachineSignalValue
            {
                Key = "Cycle",
                Type = SignalType.Digital,
                Value = cyclePulse
            }
        };

        return ValueTask.FromResult(
            new MachineSignalSnapshot(MachineId, signals, DateTimeOffset.UtcNow));
    }
}
