using FactoryConnect.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.Api;

internal static class ApiPersistenceStartup
{
    public static Task RunAsync(
        IServiceProvider services,
        Func<Task> runHost) =>
        RunAsync(
            services,
            runHost,
            ApiStartupCancellationRegistration.Create);

    internal static async Task RunAsync(
        IServiceProvider services,
        Func<Task> runHost,
        Func<IApiStartupCancellationRegistration> startupCancellationFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(runHost);
        ArgumentNullException.ThrowIfNull(startupCancellationFactory);

        var startupCancellation = startupCancellationFactory()
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
