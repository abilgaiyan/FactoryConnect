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
        Assert.Equal(3, snapshot.Signals.Count);
        Assert.Contains(snapshot.Signals, signal => signal.Key == "Running");
        Assert.Contains(snapshot.Signals, signal => signal.Key == "Fault");
        Assert.Contains(snapshot.Signals, signal => signal.Key == "Cycle");
    }
}
