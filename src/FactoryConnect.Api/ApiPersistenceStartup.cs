using FactoryConnect.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.Api;

internal static class ApiPersistenceStartup
{
    public static async Task RunAsync(
        IServiceProvider services,
        Func<CancellationToken, Task> runHost,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(runHost);

        var startupGate = services.GetRequiredService<IPersistenceStartupGate>();
        await startupGate.EnsureReadyAsync(cancellationToken);
        await runHost(cancellationToken);
    }
}
