using System.Net;

namespace FactoryConnect.Dashboard;

public static class CommissioningDashboardAccess
{
    public static bool Allows(HttpContext context, IConfiguration configuration) =>
        configuration.GetValue<bool>("Commissioning:Enabled") &&
        context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address) &&
        !context.Request.Headers.ContainsKey("Forwarded") &&
        !context.Request.Headers.Keys.Any(static key => key.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase));
}
