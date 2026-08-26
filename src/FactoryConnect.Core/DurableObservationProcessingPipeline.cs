namespace FactoryConnect.Core;

public sealed class DurableObservationProcessingPipeline
{
    private readonly ObservationProcessingRuntime _rawRuntime;
    private readonly MappedObservationProcessingRuntime _mappedRuntime;
    private readonly TimeSpan _pollingInterval;

    public DurableObservationProcessingPipeline(
        ObservationProcessingRuntime rawRuntime,
        MappedObservationProcessingRuntime mappedRuntime,
        TimeSpan pollingInterval)
    {
        ArgumentNullException.ThrowIfNull(rawRuntime);
        ArgumentNullException.ThrowIfNull(mappedRuntime);

        if (pollingInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pollingInterval),
                "Polling interval must be greater than zero.");
        }

        _rawRuntime = rawRuntime;
        _mappedRuntime = mappedRuntime;
        _pollingInterval = pollingInterval;
    }

    public async Task<bool> RunCycleAsync(
        CancellationToken cancellationToken = default)
    {
        var raw = await _rawRuntime.RunCycleAsync(cancellationToken);
        var mapped = await _mappedRuntime.RunCycleAsync(cancellationToken);

        return raw.Observations.Count > 0 ||
            mapped.Observations.Count > 0;
    }

    public async Task RunAsync(
        CancellationToken cancellationToken = default)
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
