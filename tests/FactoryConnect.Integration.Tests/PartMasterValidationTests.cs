using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class PartMasterValidationTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("86401")]
    [InlineData("0.0001")]
    public void InvalidCycleRejected(string value)
    {
        var part = new PartMasterRecord("GEAR-001", "Gear", null,
            decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), true);
        Assert.Throws<ArgumentException>(() => SqlServerPartMasterStore.Validate(part, "admin", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ValidPartAccepted()
    {
        SqlServerPartMasterStore.Validate(new("GEAR-001", "Gear", null, 600m, true),
            "admin", DateTimeOffset.UtcNow);
    }
}
