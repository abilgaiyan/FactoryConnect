using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlMigrationLockContractTests
{
    [Fact]
    public void LockResourceIsFrozen()
    {
        Assert.Equal("FactoryConnect.SqlMigration", SqlServerMigrationTransactionScope.LockResource);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(9999, 1)]
    [InlineData(10000, 1)]
    [InlineData(10001, 2)]
    [InlineData(15000, 2)]
    [InlineData(20000, 2)]
    public void PositiveTimeoutUsesExactCeilingConversion(long ticks, int expectedMilliseconds)
    {
        Assert.Equal(expectedMilliseconds, SqlMigrationLockTimeout.ToMilliseconds(TimeSpan.FromTicks(ticks)));
    }

    [Fact]
    public void MaximumSupportedTimeoutMapsToInt32Maximum()
    {
        var timeout = TimeSpan.FromTicks((long)int.MaxValue * TimeSpan.TicksPerMillisecond);

        Assert.Equal(int.MaxValue, SqlMigrationLockTimeout.ToMilliseconds(timeout));
    }

    [Fact]
    public void TimeoutAboveInt32MillisecondMaximumIsRejected()
    {
        var timeout = TimeSpan.FromTicks(
            ((long)int.MaxValue * TimeSpan.TicksPerMillisecond) + 1);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => SqlMigrationLockTimeout.ToMilliseconds(timeout));
    }

    [Fact]
    public void NegativeTimeoutIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SqlMigrationLockTimeout.ToMilliseconds(TimeSpan.FromTicks(-1)));
    }

    [Fact]
    public void InfiniteTimeoutIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SqlMigrationLockTimeout.ToMilliseconds(Timeout.InfiniteTimeSpan));
    }
}
