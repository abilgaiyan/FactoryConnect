using System.Collections.ObjectModel;

namespace FactoryConnect.Core;

public sealed class OperationalMetricProjectionProcessingRuntimeSet
{
    private readonly ReadOnlyCollection<OperationalMetricProjectionProcessingRuntime> _runtimes;

    public OperationalMetricProjectionProcessingRuntimeSet(
        IReadOnlyList<OperationalMetricProjectionProcessingRuntime> runtimes,
        TimeSpan pollingInterval)
    {
        ArgumentNullException.ThrowIfNull(runtimes);

        if (runtimes.Count == 0)
        {
            throw new ArgumentException(
                "At least one operational metric projection runtime is required.",
                nameof(runtimes));
        }

        if (runtimes.Select(static runtime => runtime.ProcessorId).Distinct().Count() != runtimes.Count)
        {
            throw new ArgumentException(
                "Operational metric projection processor identities must be unique.",
                nameof(runtimes));
        }

        if (pollingInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pollingInterval),
                "Polling interval must be greater than zero.");
        }

        _runtimes = Array.AsReadOnly(runtimes.ToArray());
        PollingInterval = pollingInterval;
    }

    public IReadOnlyList<OperationalMetricProjectionProcessingRuntime> Runtimes => _runtimes;

    public TimeSpan PollingInterval { get; }

    public async Task<bool> RunCycleAsync(CancellationToken cancellationToken = default)
    {
        var tasks = _runtimes
            .Select(runtime => runtime.RunCycleAsync(cancellationToken).AsTask())
            .ToArray();

        try
        {
            var results = await Task.WhenAll(tasks);
            return results.Any(static count => count > 0);
        }
        catch
        {
            await Task.WhenAll(
                tasks.Select(
                    static async task =>
                    {
                        try
                        {
                            await task;
                        }
                        catch
                        {
                            // Observe every machine runtime before propagating the failure.
                        }
                    }));
            throw;
        }
    }
}
