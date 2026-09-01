using FactoryConnect.Abstractions;
using FactoryConnect.Core;

namespace FactoryConnect.Edge;

public sealed class MachineShiftRosterMaterializationWorker(
    MachineShiftOccurrenceRosterMaterializationRuntimeSet runtimes,
    MachineShiftRosterMaterializationRequest request)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await runtimes.MaterializeAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
