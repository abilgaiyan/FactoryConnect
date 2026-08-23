using System.Net;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class MtConnectTransientRetryPolicyTests
{
    [Fact]
    public async Task ExecuteAsyncRetriesTransportFailure()
    {
        var delay = new RecordingDelay();
        var attempts = 0;
        var policy = CreatePolicy(delay);

        var result = await policy.ExecuteAsync<string>(
            _ =>
            {
                attempts++;

                return attempts == 1
                    ? Task.FromException<string>(
                        new HttpRequestException(
                            "Connection failed."))
                    : Task.FromResult("success");
            });

        Assert.Equal("success", result);
        Assert.Equal(2, attempts);
        Assert.Single(delay.Delays);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ExecuteAsyncRetriesTransientHttpStatus(
        HttpStatusCode statusCode)
    {
        var delay = new RecordingDelay();
        var attempts = 0;
        var policy = CreatePolicy(delay);

        await policy.ExecuteAsync(
            _ =>
            {
                attempts++;

                return attempts == 1
                    ? Task.FromException<int>(
                        new HttpRequestException(
                            "Transient HTTP failure.",
                            null,
                            statusCode))
                    : Task.FromResult(42);
            });

        Assert.Equal(2, attempts);
        Assert.Single(delay.Delays);
    }

    [Fact]
    public async Task ExecuteAsyncDoesNotRetryNonTransientHttpStatus()
    {
        var delay = new RecordingDelay();
        var attempts = 0;
        var policy = CreatePolicy(delay);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => policy.ExecuteAsync<int>(
                _ =>
                {
                    attempts++;

                    return Task.FromException<int>(
                        new HttpRequestException(
                            "Bad request.",
                            null,
                            HttpStatusCode.BadRequest));
                }));

        Assert.Equal(1, attempts);
        Assert.Empty(delay.Delays);
    }

    [Fact]
    public async Task ExecuteAsyncStopsAtMaximumAttempts()
    {
        var delay = new RecordingDelay();
        var attempts = 0;
        var policy = CreatePolicy(delay, maxAttempts: 3);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => policy.ExecuteAsync<int>(
                _ =>
                {
                    attempts++;

                    return Task.FromException<int>(
                        new HttpRequestException(
                            "Connection failed."));
                }));

        Assert.Equal(3, attempts);
        Assert.Equal(2, delay.Delays.Count);
    }

    [Fact]
    public async Task ExecuteAsyncUsesExponentialBackoff()
    {
        var delay = new RecordingDelay();
        var attempts = 0;
        var policy = CreatePolicy(
            delay,
            maxAttempts: 4,
            initialDelay: TimeSpan.FromSeconds(1),
            maximumDelay: TimeSpan.FromSeconds(30),
            jitterRatio: 0);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => policy.ExecuteAsync<int>(
                _ =>
                {
                    attempts++;

                    return Task.FromException<int>(
                        new HttpRequestException(
                            "Connection failed."));
                }));

        Assert.Equal(
            [
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(4),
            ],
            delay.Delays);
    }

    [Fact]
    public async Task ExecuteAsyncAppliesDeterministicJitterAndCap()
    {
        var delay = new RecordingDelay();
        var attempts = 0;
        var policy = CreatePolicy(
            delay,
            maxAttempts: 4,
            initialDelay: TimeSpan.FromSeconds(2),
            maximumDelay: TimeSpan.FromSeconds(5),
            jitterRatio: 0.2,
            jitterValues: [0, 1, 1]);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => policy.ExecuteAsync<int>(
                _ =>
                {
                    attempts++;

                    return Task.FromException<int>(
                        new HttpRequestException(
                            "Connection failed."));
                }));

        Assert.Equal(
            [
                TimeSpan.FromMilliseconds(1600),
                TimeSpan.FromMilliseconds(4800),
                TimeSpan.FromSeconds(5),
            ],
            delay.Delays);
    }

    [Fact]
    public async Task ExecuteAsyncPropagatesCancellationDuringBackoff()
    {
        using var cancellation =
            new CancellationTokenSource();

        var delay = new CancellingDelay(cancellation);
        var policy = CreatePolicy(delay);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => policy.ExecuteAsync<int>(
                _ => Task.FromException<int>(
                    new HttpRequestException(
                        "Connection failed.")),
                cancellation.Token));
    }

    [Fact]
    public async Task ExecuteAsyncDoesNotRetryOtherFailures()
    {
        var delay = new RecordingDelay();
        var attempts = 0;
        var policy = CreatePolicy(delay);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => policy.ExecuteAsync<int>(
                _ =>
                {
                    attempts++;

                    return Task.FromException<int>(
                        new InvalidDataException(
                            "Malformed protocol data."));
                }));

        Assert.Equal(1, attempts);
        Assert.Empty(delay.Delays);
    }

    private static MtConnectTransientRetryPolicy CreatePolicy(
        IMtConnectRetryDelay delay,
        int maxAttempts = 3,
        TimeSpan? initialDelay = null,
        TimeSpan? maximumDelay = null,
        double jitterRatio = 0,
        params double[] jitterValues)
    {
        return new MtConnectTransientRetryPolicy(
            new MtConnectRetryOptions(
                maxAttempts,
                initialDelay ?? TimeSpan.FromSeconds(1),
                maximumDelay ?? TimeSpan.FromSeconds(30),
                jitterRatio),
            delay,
            new SequenceJitterSource(jitterValues));
    }

    private sealed class RecordingDelay :
        IMtConnectRetryDelay
    {
        public List<TimeSpan> Delays { get; } = [];

        public Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(delay);

            return Task.CompletedTask;
        }
    }

    private sealed class CancellingDelay(
        CancellationTokenSource cancellation)
        : IMtConnectRetryDelay
    {
        public Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();

            return Task.CompletedTask;
        }
    }

    private sealed class SequenceJitterSource(
        params double[] values)
        : IMtConnectJitterSource
    {
        private readonly Queue<double> _values = new(values);

        public double NextDouble()
        {
            return _values.Count == 0
                ? 0.5
                : _values.Dequeue();
        }
    }
}
