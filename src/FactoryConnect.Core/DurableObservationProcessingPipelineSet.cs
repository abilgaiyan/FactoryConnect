namespace FactoryConnect.Core;

public sealed class DurableObservationProcessingPipelineSet
{
    private readonly DurableObservationProcessingPipeline[] _pipelines;
    private readonly TimeSpan _pollingInterval;

    public DurableObservationProcessingPipelineSet(
        IReadOnlyList<DurableObservationProcessingPipeline> pipelines,
        TimeSpan pollingInterval)
    {
        ArgumentNullException.ThrowIfNull(pipelines);

        if (pipelines.Count == 0)
        {
            throw new ArgumentException(
                "At least one durable observation processing pipeline is required.",
                nameof(pipelines));
        }

        if (pollingInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pollingInterval),
                "Polling interval must be greater than zero.");
        }

        _pipelines = pipelines.ToArray();
        _pollingInterval = pollingInterval;
    }

    public IReadOnlyList<DurableObservationProcessingPipeline> Pipelines =>
        _pipelines;

    public async Task<bool> RunCycleAsync(
        CancellationToken cancellationToken = default)
    {
        var processed = false;

        foreach (var pipeline in _pipelines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed = await pipeline.RunCycleAsync(cancellationToken) || processed;
        }

        return processed;
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
