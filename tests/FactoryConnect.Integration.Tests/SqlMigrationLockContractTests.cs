using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlMigrationLockContractTests
{
    [Fact]
    public void LockResourceIsFrozen()
    {
        Assert.Equal("FactoryConnect.SqlMigration", SqlServerMigrationTransactionScope.LockResource);
    }

    [Fact]
    public void LockCommandHasNoIndependentCommandTimeout()
    {
        Assert.Equal(0, SqlServerMigrationTransactionScope.LockCommandTimeoutSeconds);
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

    [Fact]
    public void SqlIntLockResultIsAcceptedExactly()
    {
        Assert.Equal(1, SqlServerMigrationTransactionScope.RequireLockReturnCode(1));
    }

    [Fact]
    public void MissingLockResultIsRejected()
    {
        var exception = Assert.Throws<SqlMigrationLockAcquisitionException>(
            () => SqlServerMigrationTransactionScope.RequireLockReturnCode(null));

        Assert.Null(exception.ReturnCode);
    }

    [Fact]
    public void NonSqlIntLockResultIsRejected()
    {
        var exception = Assert.Throws<SqlMigrationLockAcquisitionException>(
            () => SqlServerMigrationTransactionScope.RequireLockReturnCode(0L));

        Assert.Null(exception.ReturnCode);
    }
}
