using FactoryConnect.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.Api;

internal static class ApiPersistenceStartup
{
    public static async Task RunAsync(
        IServiceProvider services,
        Func<Task> runHost)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(runHost);

        var startupCancellationFactory = services
            .GetRequiredService<IApiStartupCancellationRegistrationFactory>();
        var startupCancellation = startupCancellationFactory.Create()
            ?? throw new InvalidOperationException(
                "API startup cancellation factory returned no registration.");

        try
        {
            var startupGate = services.GetRequiredService<IPersistenceStartupGate>();
            await startupGate.EnsureReadyAsync(startupCancellation.Token);
        }
        finally
        {
            startupCancellation.Dispose();
        }

        await runHost();
    }
}
