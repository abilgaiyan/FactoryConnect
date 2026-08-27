using System.Collections.ObjectModel;

namespace FactoryConnect.Core;

public sealed class MetricAggregationProcessingRuntimeSet
{
    private readonly ReadOnlyCollection<MetricAggregationProcessingRuntime> _runtimes;

    public MetricAggregationProcessingRuntimeSet(
        IReadOnlyList<MetricAggregationProcessingRuntime> runtimes,
        TimeSpan pollingInterval)
    {
        ArgumentNullException.ThrowIfNull(runtimes);

        if (runtimes.Count == 0)
        {
            throw new ArgumentException(
                "At least one metric aggregation runtime is required.",
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

    public IReadOnlyList<MetricAggregationProcessingRuntime> Runtimes => _runtimes;

    public TimeSpan PollingInterval { get; }

    public async Task<bool> RunCycleAsync(
        CancellationToken cancellationToken = default)
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
                            // Ensure every runtime is observed before propagating.
                        }
                    }));
            throw;
        }
    }
}
