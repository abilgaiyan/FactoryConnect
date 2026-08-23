using System.Net;

namespace FactoryConnect.Edge;

public sealed class MtConnectTransientRetryPolicy
{
    private static readonly Action<
        ILogger,
        int,
        int,
        double,
        string,
        Exception?> RetryScheduled =
            LoggerMessage.Define<int, int, double, string>(
                LogLevel.Warning,
                new EventId(3, nameof(RetryScheduled)),
                "MTConnect acquisition attempt {Attempt} of {MaxAttempts} " +
                "failed with {Failure}; retrying in {DelayMilliseconds} ms.");

    private readonly MtConnectRetryOptions _options;
    private readonly IMtConnectRetryDelay _delay;
    private readonly IMtConnectJitterSource _jitter;
    private readonly ILogger<MtConnectTransientRetryPolicy> _logger;

    public MtConnectTransientRetryPolicy(
        MtConnectRetryOptions options,
        IMtConnectRetryDelay delay,
        IMtConnectJitterSource jitter,
        ILogger<MtConnectTransientRetryPolicy> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(delay);
        ArgumentNullException.ThrowIfNull(jitter);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _delay = delay;
        _jitter = jitter;
        _logger = logger;
    }

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation(cancellationToken);
            }
            catch (HttpRequestException exception)
                when (attempt < _options.MaxAttempts &&
                      IsTransient(exception))
            {
                var retryDelay = CalculateDelay(attempt);
                var failure = exception.StatusCode?.ToString()
                    ?? "transport failure";

                RetryScheduled(
                    _logger,
                    attempt,
                    _options.MaxAttempts,
                    retryDelay.TotalMilliseconds,
                    failure,
                    exception);

                await _delay.DelayAsync(
                    retryDelay,
                    cancellationToken);
            }
        }
    }

    private TimeSpan CalculateDelay(int failedAttempt)
    {
        var exponent = Math.Pow(2, failedAttempt - 1);
        var exponentialTicks =
            _options.InitialDelay.Ticks * exponent;

        var cappedTicks = Math.Min(
            exponentialTicks,
            _options.MaximumDelay.Ticks);

        var jitterOffset =
            ((_jitter.NextDouble() * 2) - 1) *
            _options.JitterRatio;

        var jitteredTicks = cappedTicks * (1 + jitterOffset);
        var finalTicks = Math.Clamp(
            jitteredTicks,
            0,
            _options.MaximumDelay.Ticks);

        return TimeSpan.FromTicks((long)finalTicks);
    }

    private static bool IsTransient(
        HttpRequestException exception)
    {
        if (exception.StatusCode is null)
        {
            return true;
        }

        return exception.StatusCode is
            HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests ||
            (int)exception.StatusCode is >= 500 and <= 599;
    }
}
