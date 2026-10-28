using System.Net;
using FactoryConnect.Dashboard;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace FactoryConnect.Dashboard.Tests;

public sealed class CommissioningAccessTests
{
    [Theory]
    [InlineData(false, "127.0.0.1", false, false)]
    [InlineData(true, "192.168.1.2", false, false)]
    [InlineData(true, "127.0.0.1", true, false)]
    [InlineData(true, "127.0.0.1", false, true)]
    public void GatewayCannotExposeLocalDiagnosticsToRemoteClients(bool enabled, string ip, bool forwarded, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        if (forwarded) context.Request.Headers["Forwarded"] = "for=192.168.1.2";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Commissioning:Enabled"] = enabled.ToString() }).Build();
        Assert.Equal(expected, CommissioningDashboardAccess.Allows(context, configuration));
    }
}
