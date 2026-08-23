using System.Net;
using FactoryConnect.Abstractions;
using FactoryConnect.Protocols.MTConnect;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class MtConnectAcquisitionRuntimeTests
{
    [Fact]
    public async Task RunCycleAsyncAcquiresAndPublishesBatch()
    {
        var handler = new SequenceHandler(
            SampleResponse(42, 110, 111));

        using var httpClient = new HttpClient(handler);
        var sink = new RecordingSink();
        var runtime = CreateRuntime(httpClient, sink, 101);

        var result = await runtime.RunCycleAsync();

        Assert.Same(result, Assert.Single(sink.Results));
        Assert.Equal(111UL, result.NextSequence);
        Assert.Equal(
            "http://localhost:5000/sample?from=101",
            Assert.Single(handler.RequestUris).AbsoluteUri);
    }

    [Fact]
    public async Task RunAsyncContinuesFromSessionCursor()
    {
        var handler = new SequenceHandler(
            SampleResponse(42, 110, 111),
            SampleResponse(42, 120, 121));

        using var httpClient = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();

        var sink = new RecordingSink(
            onWrite: () =>
            {
                if (handler.RequestUris.Count == 2)
                {
                    cancellation.Cancel();
                }
            });

        var runtime = CreateRuntime(httpClient, sink, 101);

        await runtime.RunAsync(cancellation.Token);

        Assert.Equal(2, sink.Results.Count);
        Assert.Equal(
            "http://localhost:5000/sample?from=101",
            handler.RequestUris[0].AbsoluteUri);
        Assert.Equal(
            "http://localhost:5000/sample?from=111",
            handler.RequestUris[1].AbsoluteUri);
    }

    [Fact]
    public async Task RunAsyncStopsWhenPollingDelayIsCancelled()
    {
        var handler = new SequenceHandler(
            SampleResponse(42, 110, 111));

        using var httpClient = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();

        var sink = new RecordingSink(
            onWrite: cancellation.Cancel);

        var runtime = CreateRuntime(httpClient, sink, 101);

        await runtime.RunAsync(cancellation.Token);

        Assert.Single(sink.Results);
        Assert.Single(handler.RequestUris);
    }

    [Fact]
    public async Task RunCycleAsyncDoesNotPublishFailedAcquisition()
    {
        var handler = new SequenceHandler(
            new HttpResponseMessage(
                HttpStatusCode.ServiceUnavailable));

        using var httpClient = new HttpClient(handler);
        var sink = new RecordingSink();
        var runtime = CreateRuntime(httpClient, sink, 101);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => runtime.RunCycleAsync());

        Assert.Empty(sink.Results);
    }


    [Fact]
    public async Task RunCycleAsyncRetriesSameCursorBeforePublishing()
    {
        var handler = new SequenceHandler(
            new HttpResponseMessage(
                HttpStatusCode.ServiceUnavailable),
            SampleResponse(42, 110, 111));

        using var httpClient = new HttpClient(handler);
        var sink = new RecordingSink();
        var runtime = CreateRuntime(
            httpClient,
            sink,
            101,
            maxAttempts: 2);

        await runtime.RunCycleAsync();

        Assert.Equal(2, handler.RequestUris.Count);
        Assert.All(
            handler.RequestUris,
            uri => Assert.Equal(
                "http://localhost:5000/sample?from=101",
                uri.AbsoluteUri));
        Assert.Single(sink.Results);
    }

    [Fact]
    public async Task RunCycleAsyncDoesNotRetrySinkFailure()
    {
        var handler = new SequenceHandler(
            SampleResponse(42, 110, 111));

        using var httpClient = new HttpClient(handler);
        var runtime = CreateRuntime(
            httpClient,
            new FailingSink(),
            101,
            maxAttempts: 3);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.RunCycleAsync());

        Assert.Single(handler.RequestUris);
    }

    [Fact]
    public async Task RunCycleAsyncPropagatesAcquisitionCancellation()
    {
        using var httpClient = new HttpClient(
            new CancellingHandler());

        var runtime = CreateRuntime(
            httpClient,
            new RecordingSink(),
            101);

        using var cancellation =
            new CancellationTokenSource();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runtime.RunCycleAsync(cancellation.Token));
    }

    [Fact]
    public void ConstructorRejectsNonPositivePollingInterval()
    {
        using var httpClient = new HttpClient(
            new SequenceHandler());

        Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateRuntime(
                httpClient,
                new RecordingSink(),
                101,
                TimeSpan.Zero));
    }

    private static MtConnectAcquisitionRuntime CreateRuntime(
        HttpClient httpClient,
        IMtConnectObservationSink sink,
        ulong fromSequence,
        TimeSpan? pollingInterval = null,
        int maxAttempts = 1)
    {
        return new MtConnectAcquisitionRuntime(
            new MtConnectAcquisitionSession(
                new MtConnectSampleClient(httpClient),
                fromSequence),
            new MtConnectEndpoint(
                new Uri("http://localhost:5000")),
            MachineId.New(),
            "CNC-01",
            new MtConnectTransientRetryPolicy(
                new MtConnectRetryOptions(
                    maxAttempts,
                    TimeSpan.FromMilliseconds(1),
                    TimeSpan.FromMilliseconds(1),
                    0),
                new ImmediateRetryDelay(),
                new FixedJitterSource(),
                NullLogger<MtConnectTransientRetryPolicy>.Instance),
            sink,
            pollingInterval ?? TimeSpan.FromMilliseconds(1));
    }

    private static HttpResponseMessage SampleResponse(
        ulong instanceId,
        ulong lastSequence,
        ulong nextSequence)
    {
        var xml = $"""
            <MTConnectStreams xmlns="urn:mtconnect.org:MTConnectStreams:2.5">
              <Header instanceId="{instanceId}"
                      firstSequence="1"
                      lastSequence="{lastSequence}"
                      nextSequence="{nextSequence}" />
              <Streams>
                <DeviceStream name="CNC-01" uuid="uuid-1">
                  <ComponentStream component="Controller" componentId="c1">
                    <Events>
                      <Execution dataItemId="exec"
                                 timestamp="2026-08-23T10:00:00Z"
                                 sequence="{lastSequence}">ACTIVE</Execution>
                    </Events>
                  </ComponentStream>
                </DeviceStream>
              </Streams>
            </MTConnectStreams>
            """;

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(xml),
        };
    }

    private sealed class RecordingSink(
        Action? onWrite = null) : IMtConnectObservationSink
    {
        public List<MtConnectSampleResult> Results { get; } = [];

        public ValueTask WriteAsync(
            MtConnectSampleResult result,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Results.Add(result);
            onWrite?.Invoke();

            return ValueTask.CompletedTask;
        }
    }


    private sealed class ImmediateRetryDelay :
        IMtConnectRetryDelay
    {
        public Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.CompletedTask;
        }
    }

    private sealed class FixedJitterSource :
        IMtConnectJitterSource
    {
        public double NextDouble() => 0.5;
    }

    private sealed class FailingSink :
        IMtConnectObservationSink
    {
        public ValueTask WriteAsync(
            MtConnectSampleResult result,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "Sink failed.");
        }
    }

    private sealed class CancellingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class SequenceHandler(
        params HttpResponseMessage[] responses)
        : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses =
            new(responses);

        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri is not null)
            {
                RequestUris.Add(request.RequestUri);
            }

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException(
                    "No HTTP response configured for request.");
            }

            var response = _responses.Dequeue();
            response.RequestMessage = request;

            return Task.FromResult(response);
        }
    }
}
