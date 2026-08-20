using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Tests;

public sealed class MachineIdTests
{
    [Fact]
    public void NewCreatesNonEmptyId()
    {
        var id = MachineId.New();

        Assert.False(id.IsEmpty);
    }
}
