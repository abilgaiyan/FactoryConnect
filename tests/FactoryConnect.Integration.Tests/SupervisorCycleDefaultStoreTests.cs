using FactoryConnect.Persistence.SqlServer;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class SupervisorCycleDefaultStoreTests
{
    [Fact]
    public void RejectsInvalidContext()
    {
        var start = new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);
        var context = new SupervisorCycleContext(Guid.NewGuid(), "assignment", "shift",
            start, start.AddHours(8), "WO-1", "GEAR-001", "OP-10");
        SqlServerSupervisorCycleDefaultStore.Validate(context);
        Assert.Throws<ArgumentException>(() => SqlServerSupervisorCycleDefaultStore.Validate(context with { PartNumber = "" }));
        Assert.Throws<ArgumentException>(() => SqlServerSupervisorCycleDefaultStore.Validate(context with { Operation = " OP-10" }));
        Assert.Throws<ArgumentException>(() => SqlServerSupervisorCycleDefaultStore.Validate(context with { EndsAtUtc = start }));
    }
}
