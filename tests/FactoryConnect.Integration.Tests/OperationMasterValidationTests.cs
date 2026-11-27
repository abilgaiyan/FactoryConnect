using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class OperationMasterValidationTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("86401")]
    [InlineData("0.0001")]
    public void InvalidOverrideRejected(string value)
    {
        var operation = new OperationMasterRecord("GEAR-001", "TURN", "Turning",
            decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), true);
        Assert.Throws<ArgumentException>(() => SqlServerOperationMasterStore.Validate(operation, "admin", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void OptionalOverrideAccepted()
    {
        SqlServerOperationMasterStore.Validate(new("GEAR-001", "TURN", "Turning", null, true),
            "admin", DateTimeOffset.UtcNow);
        SqlServerOperationMasterStore.Validate(new("GEAR-001", "TURN", "Turning", 450m, true),
            "admin", DateTimeOffset.UtcNow);
    }

    [Theory]
    [InlineData("", "TURN")]
    [InlineData("GEAR-001", "")]
    [InlineData(" GEAR-001", "TURN")]
    [InlineData("GEAR-001", "TURN ")]
    public void InvalidIdentityRejected(string part, string code)
    {
        Assert.Throws<ArgumentException>(() => SqlServerOperationMasterStore.Validate(
            new(part, code, "Turning", null, true), "admin", DateTimeOffset.UtcNow));
    }
}
