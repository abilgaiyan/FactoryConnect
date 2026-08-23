using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class MtConnectRetryOptionsTests
{
    [Fact]
    public void ConstructorPreservesConfiguration()
    {
        var options = new MtConnectRetryOptions(
            3,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(30),
            0.2);

        Assert.Equal(3, options.MaxAttempts);
        Assert.Equal(
            TimeSpan.FromSeconds(1),
            options.InitialDelay);
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            options.MaximumDelay);
        Assert.Equal(0.2, options.JitterRatio);
    }

    [Fact]
    public void ConstructorRejectsMaximumAttemptsBelowOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MtConnectRetryOptions(
                0,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(30),
                0.2));
    }

    [Fact]
    public void ConstructorRejectsMaximumDelayBelowInitialDelay()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MtConnectRetryOptions(
                3,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(1),
                0.2));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ConstructorRejectsInvalidJitterRatio(
        double jitterRatio)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MtConnectRetryOptions(
                3,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(30),
                jitterRatio));
    }
}
