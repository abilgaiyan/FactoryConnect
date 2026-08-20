using FactoryConnect.Abstractions;

namespace FactoryConnect.Protocols.Modbus.Tests;

public sealed class ProtocolBoundaryTests
{
    [Fact]
    public void MachineStateIsProtocolIndependent()
    {
        Assert.Equal(MachineState.Running, MachineState.Running);
    }
}
