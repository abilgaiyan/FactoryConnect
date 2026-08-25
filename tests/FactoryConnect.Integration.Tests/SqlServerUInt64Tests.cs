using System.Data;
using FactoryConnect.Persistence.SqlServer;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlServerUInt64Tests
{
    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(ulong.MaxValue)]
    public void ParameterUsesDecimalTwentyZeroMapping(ulong value)
    {
        using var parameter = SqlServerUInt64.CreateParameter(
            "@Value",
            value);

        Assert.Equal(SqlDbType.Decimal, parameter.SqlDbType);
        Assert.Equal(20, parameter.Precision);
        Assert.Equal(0, parameter.Scale);
        Assert.Equal((decimal)value, parameter.Value);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(ulong.MaxValue)]
    public void MaterializationRoundTripsUInt64(ulong value)
    {
        Assert.Equal(
            value,
            SqlServerUInt64.Materialize((decimal)value));
    }
}
