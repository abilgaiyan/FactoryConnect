using FactoryConnect.Api.Machines;
using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class CounterProfileIntervalGuardTests
{
    private static readonly Guid Machine = Guid.Parse("bd4503b8-2f65-4fd1-b5f9-11db5aaf6d41");
    private static readonly DateTimeOffset Start = new(2026, 11, 23, 0, 0, 0, TimeSpan.Zero);

    private static MachineProductionEvidenceProfile Profile(int revision, DateTimeOffset effective) =>
        new(Guid.NewGuid(), Machine, revision, "CounterOnly", null, "count", "CumulativeResettable",
            effective, "test", Start);

    [Fact]
    public void StableProfileAcrossIntervalIsSelected()
    {
        var first = Profile(1, Start.AddDays(-1));
        var next = Profile(2, Start.AddHours(1));
        Assert.Equal(first, CounterProfileIntervalGuard.EffectiveUnchanged(
            [first, next], Start, Start.AddHours(1)));
    }

    [Fact]
    public void MidIntervalProfileChangeFailsClosed()
    {
        var first = Profile(1, Start.AddDays(-1));
        var next = Profile(2, Start.AddMinutes(30));
        Assert.Null(CounterProfileIntervalGuard.EffectiveUnchanged(
            [first, next], Start, Start.AddHours(1)));
    }

    [Fact]
    public void MissingProfileAtStartFailsClosed()
    {
        Assert.Null(CounterProfileIntervalGuard.EffectiveUnchanged(
            [Profile(1, Start.AddMinutes(1))], Start, Start.AddHours(1)));
    }
}
