using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Tests;

public sealed class MachineIdTests
{
    [Fact]
    public void New_creates_non_empty_id()
    {
        var id = MachineId.New();

        Assert.False(id.IsEmpty);
    }
}
