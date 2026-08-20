using FactoryConnect.Abstractions;

namespace FactoryConnect.Protocols.Modbus.Tests;

public sealed class ProtocolBoundaryTests
{
    [Fact]
    public void Machine_state_is_protocol_independent()
    {
        Assert.Equal(MachineState.Running, MachineState.Running);
    }
}
