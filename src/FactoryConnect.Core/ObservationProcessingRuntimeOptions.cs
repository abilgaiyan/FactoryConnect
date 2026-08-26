namespace FactoryConnect.Core;

public sealed record ObservationProcessingRuntimeOptions
{
    public ObservationProcessingRuntimeOptions(
        int batchSize,
        TimeSpan pollingInterval)
    {
        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(batchSize),
                "Batch size must be greater than zero.");
        }

        if (pollingInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pollingInterval),
                "Polling interval must be greater than zero.");
        }

        BatchSize = batchSize;
        PollingInterval = pollingInterval;
    }

    public int BatchSize { get; }

    public TimeSpan PollingInterval { get; }
}
