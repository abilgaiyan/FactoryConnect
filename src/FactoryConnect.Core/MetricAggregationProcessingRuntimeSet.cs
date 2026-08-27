namespace FactoryConnect.Core;

public sealed class MetricAggregationProcessingRuntimeSet
{
    private readonly MetricAggregationProcessingRuntime[] _runtimes;
    private readonly TimeSpan _pollingInterval;

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

        _runtimes = runtimes.ToArray();
        _pollingInterval = pollingInterval;
    }

    public IReadOnlyList<MetricAggregationProcessingRuntime> Runtimes => _runtimes;

    public async Task<bool> RunCycleAsync(
        CancellationToken cancellationToken = default)
    {
        var processed = false;

        foreach (var runtime in _runtimes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed = await runtime.RunCycleAsync(cancellationToken) > 0 || processed;
        }

        return processed;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (await RunCycleAsync(cancellationToken))
            {
                continue;
            }

            try
            {
                await Task.Delay(_pollingInterval, cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
