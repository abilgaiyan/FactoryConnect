using FactoryConnect.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.Edge;

internal static class EdgePersistenceStartup
{
    public static async Task RunAsync(
        IServiceProvider services,
        Func<Task> runHost,
        IEdgeStartupCancellationRegistrationFactory startupCancellationFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(runHost);
        ArgumentNullException.ThrowIfNull(startupCancellationFactory);

        using var startupCancellation = startupCancellationFactory.Create()
            ?? throw new InvalidOperationException(
                "Edge startup cancellation factory returned no registration.");

        var startupGate = services.GetRequiredService<IPersistenceStartupGate>();
        await startupGate.EnsureReadyAsync(startupCancellation.Token);

        startupCancellation.Dispose();
        await runHost();
    }
}
