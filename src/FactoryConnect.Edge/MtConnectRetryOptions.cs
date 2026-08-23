namespace FactoryConnect.Edge;

public sealed record MtConnectRetryOptions
{
    public int MaxAttempts { get; }

    public TimeSpan InitialDelay { get; }

    public TimeSpan MaximumDelay { get; }

    public double JitterRatio { get; }

    public MtConnectRetryOptions(
        int maxAttempts,
        TimeSpan initialDelay,
        TimeSpan maximumDelay,
        double jitterRatio)
    {
        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxAttempts),
                maxAttempts,
                "Maximum attempts must be at least one.");
        }

        if (initialDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(initialDelay),
                initialDelay,
                "Initial delay must be greater than zero.");
        }

        if (maximumDelay < initialDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumDelay),
                maximumDelay,
                "Maximum delay must not be less than the initial delay.");
        }

        if (!double.IsFinite(jitterRatio) ||
            jitterRatio is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(jitterRatio),
                jitterRatio,
                "Jitter ratio must be between zero and one.");
        }

        MaxAttempts = maxAttempts;
        InitialDelay = initialDelay;
        MaximumDelay = maximumDelay;
        JitterRatio = jitterRatio;
    }
}
