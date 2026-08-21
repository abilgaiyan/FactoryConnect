using FactoryConnect.Abstractions;
using FactoryConnect.Simulator;

namespace FactoryConnect.Integration.Tests;

public sealed class SimulatorConnectorTests
{
    [Fact]
    public async Task ReadSignalsAsyncReturnsCanonicalMachineSignals()
    {
        var machineId = MachineId.New();
        var connector = new SimulatedMachineConnector(machineId);

        var snapshot = await connector.ReadSignalsAsync();

        Assert.Equal(machineId, snapshot.MachineId);
        Assert.Equal(4, snapshot.Signals.Count);
        Assert.Contains(snapshot.Signals, signal => signal.Key == CanonicalSignalKeys.Running);
        Assert.Contains(snapshot.Signals, signal => signal.Key == CanonicalSignalKeys.Idle);
        Assert.Contains(snapshot.Signals, signal => signal.Key == CanonicalSignalKeys.Fault);
        Assert.Contains(snapshot.Signals, signal => signal.Key == CanonicalSignalKeys.CycleCount);
    }
}
